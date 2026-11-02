import { test, expect } from '@playwright/test'
import { dispenser, face, open, PIN, pumpApi, resetWorld, selectPump, startFueling, state, station, stationState, tapCardAtPump } from './helpers'

// One forecourt controller, two pumps, one window with a tab per pump. The point of the second pump is what one
// hides: two sales asking one payment worker at once, one journal for both, and a tab strip that shows the pump
// you are not looking at.
test.describe('two pumps under one controller', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('the station reports two pumps, each with its own id, node, port, and dispenser process', async ({ request }) => {
    const s = await stationState(request)
    expect(s.pumpCount).toBe(2)
    expect(s.pumps.map((p: { pumpId: string }) => p.pumpId)).toEqual(['PUMP-01', 'PUMP-02'])
    expect(s.pumps.map((p: { node: string }) => p.node)).toEqual(['2.1', '2.2'])
    expect(s.pumps.map((p: { dispenser: { port: number } }) => p.dispenser.port)).toEqual([5178, 5179])
    const pids = s.pumps.map((p: { dispenser: { pid: number } }) => p.dispenser.pid)
    expect(pids[0]).not.toBe(pids[1])
    expect(s.pumps.every((p: { dispenser: { connected: boolean } }) => p.dispenser.connected)).toBe(true)
  })

  test('a sale on pump 2 while pump 1 fuels: both settle to the cent and the tab chips track both', async ({ page, request }) => {
    // Pump 1: card, grade, lift, insert, squeeze. Fuel is moving.
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    const sale1 = (await state(request, 1)).sale.saleId
    expect(sale1).toMatch(/^P1-S[0-9]{4}$/)

    // Switch to pump 2. Pump 1's chip keeps reporting while its tab is hidden.
    await selectPump(page, 2)
    await expect(page.getByTestId('pump-tab-hint-1')).toContainText('fueling')
    await expect(page.getByTestId('pump-tab-hint-2')).toContainText('idle')
    await expect(dispenser(page)).toContainText('PUMP-02')

    // Pump 2: a debit card at this pump's own reader, then a short pour and a hang.
    await tapCardAtPump(page, 'DebitGoodPin', 'Debit')
    await expect(page.getByTestId('pump-tab-hint-2')).toContainText('readytopump')
    await startFueling(page, 'Plus')
    await expect(page.getByTestId('pump-tab-hint-2')).toContainText('fueling')
    const sale2 = (await state(request, 2)).sale.saleId
    expect(sale2).toMatch(/^P2-S[0-9]{4}$/)
    // The one bank holds money for both sales, by their ids.
    await expect.poll(async () => (await stationState(request)).openHolds.slice().sort()).toEqual([sale1, sale2].sort())

    await expect.poll(async () => (await state(request, 2)).sale.cents).toBeGreaterThan(100)
    await dispenser(page).getByRole('button', { name: 'Hang nozzle' }).click()
    await expect(face(page)).toContainText('Thank you', { timeout: 30_000 })
    await expect.poll(async () => (await state(request, 2)).sale.hold).toBe('Settled')
    const p2 = (await state(request, 2)).sale
    expect(p2.grade).toBe('Plus')
    expect(p2.tender).toBe('Debit')

    // Pump 1 ran to its $30 ceiling the whole time, untouched by any of it.
    await selectPump(page, 1)
    await expect(face(page)).toContainText('Thank you', { timeout: 120_000 })
    const p1 = (await state(request, 1)).sale
    expect(p1.cents).toBe(3000)
    expect(p1.hold).toBe('Settled')
    expect((await stationState(request)).openHolds).toHaveLength(0)

    // The station's one ledger names the pump on every sale line.
    await station(page).getByRole('tab', { name: /Ledger/ }).click()
    await expect(station(page).getByText(new RegExp(`sale\\.close.*${sale1}`)).first()).toBeVisible()
    // Pump 2's lines are on pump 2's tab, and nowhere else.
    await expect(station(page).getByText(sale2)).toHaveCount(0)
    await selectPump(page, 2)
    await station(page).getByRole('tab', { name: /Ledger/ }).click()
    await expect(station(page).getByText(new RegExp(`sale\\.close.*${sale2}`)).first()).toBeVisible()
  })

  test('closing pump 2 for sales leaves pump 1 selling', async ({ page, request }) => {
    await selectPump(page, 2)
    const s = station(page)
    await s.getByRole('tab', { name: /Power/ }).click()
    await s.getByRole('button', { name: 'Close pump for sales' }).click()
    await expect(face(page)).toContainText('Out of service: pump switch')
    await expect.poll(async () => (await state(request, 2)).pump.phase).toBe('Closed')
    await expect(page.getByTestId('pump-tab-hint-2')).toContainText('out of service')
    expect((await state(request, 1)).pump.inService).toBe(true)

    await selectPump(page, 1)
    await expect(face(page)).not.toContainText('Out of service')
    await tapCardAtPump(page, 'Good')
    expect((await stationState(request)).openHolds).toEqual([(await state(request, 1)).sale.saleId])
    expect((await state(request, 1)).sale.saleId).toMatch(/^P1-S/)

    await selectPump(page, 2)
    await s.getByRole('button', { name: 'Open pump for sales' }).click()
    await expect.poll(async () => (await state(request, 2)).pump.inService).toBe(true)
  })

  test('a price written by the back office reaches both islands', async ({ page, request }) => {
    const r = await request.post(`${pumpApi(1, '').replace('/pump/1', '')}/admin/prices`, { data: { pin: PIN, grade: 'Premium', mills: 4321 } })
    expect(r.ok()).toBeTruthy()
    await expect.poll(async () => (await state(request, 1)).pump.prices.Premium).toBe(4321)
    await expect.poll(async () => (await state(request, 2)).pump.prices.Premium).toBe(4321)
    await expect(dispenser(page).getByRole('button', { name: /^Premium / })).toContainText('$4.321')
    await selectPump(page, 2)
    await expect(dispenser(page).getByRole('button', { name: /^Premium / })).toContainText('$4.321')
    // Put it back for the next spec.
    await request.post(`${pumpApi(1, '').replace('/pump/1', '')}/admin/prices`, { data: { pin: PIN, grade: 'Premium', mills: 4099 } })
  })

  test('pulling pump 1 dispenser plug faults only pump 1 sale', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    await selectPump(page, 2)
    await tapCardAtPump(page, 'Good')
    await startFueling(page)

    await request.post(pumpApi(1, '/dispenser/powerloss'))
    await expect(page.getByTestId('pump-tab-hint-1')).toContainText('out of service')
    await expect.poll(async () => (await state(request, 1)).sale.faultCode).toBe('LINK.DISPENSER.LOST')
    // Pump 2 kept pouring through it.
    expect((await state(request, 2)).sale.state).toBe('Fueling')
    expect((await state(request, 2)).dispenser.connected).toBe(true)
    await expect(face(page)).toContainText('Fueling')

    await request.post(pumpApi(1, '/dispenser/poweron'))
    await expect.poll(async () => (await state(request, 1)).dispenser.connected, { timeout: 20_000 }).toBe(true)
    await expect.poll(async () => (await state(request, 1)).sale.hold).toBe('Settled')
  })
})
