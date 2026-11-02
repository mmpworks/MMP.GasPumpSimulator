import { test, expect } from '@playwright/test'
import { dispenser, face, open, pumpApi, resetWorld, startFueling, state, station, tapCardAtPump } from "./helpers"

// Steve's defect: after a drive-off he pressed the attendant's reset, hung the nozzle, and the face still said
// "Hose breakaway". A fault is a latched service condition now; the repair clears it on both sides of the seam.
test.describe('faults and their repairs', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('a drive-off faults the sale, the attendant reset clears the screen, and the next card is admitted', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    const d = dispenser(page)
    await d.getByRole('button', { name: 'Drive-off (breakaway)' }).click()
    await expect(face(page)).toContainText('Hose breakaway')
    await expect(d).toContainText('OUT OF SERVICE')
    await expect(d).toContainText('PUMP.HOSE.BREAKAWAY')
    await expect.poll(async () => (await state(request)).sale.hold).toBe('Settled')

    // The attendant fixes the coupling. The face must stop saying breakaway at once, with no nozzle action needed.
    await d.getByRole('button', { name: 'Attendant resets the breakaway' }).click()
    await expect(face(page)).toContainText('Pump back in service')
    await expect(face(page)).not.toContainText('Hose breakaway')
    await expect(d).not.toContainText('OUT OF SERVICE')
    await expect.poll(async () => (await state(request)).pump.phase).toBe('Disarmed')
    expect((await state(request)).pump.inService).toBe(true)

    // The next driver is served.
    await tapCardAtPump(page, 'DebitGoodPin', 'Debit')
  })

  test('the operator closes the pump for sales, both readers refuse, and opening it admits the next card', async ({ page, request }) => {
    const d = dispenser(page)
    const s = station(page)
    await s.getByRole('tab', { name: /Power/ }).click()
    await s.getByRole('button', { name: 'Close pump for sales' }).click()
    await expect(face(page)).toContainText('Out of service: pump switch')
    await expect.poll(async () => (await state(request)).pump.phase).toBe('Closed')
    await d.getByRole('button', { name: 'Tap' }).click()
    await expect(d.locator('.reader-note')).toContainText(/out of service/i)
    expect((await state(request)).openHolds).toHaveLength(0)

    // The lift button is shut while the pump is closed; the unit tests cover a lift that arrives anyway.
    await expect(d.getByRole('button', { name: /^Lift nozzle/ })).toBeDisabled()

    await s.getByRole('button', { name: 'Open pump for sales' }).click()
    await expect.poll(async () => (await state(request)).pump.inService).toBe(true)
    await tapCardAtPump(page, 'Good')
  })

  test('a lifted nozzle with no sale is a calling pump, and a card then arms it without a second lift', async ({ page, request }) => {
    const d = dispenser(page)
    await d.getByRole('button', { name: /^Lift nozzle/ }).click()
    await expect.poll(async () => (await state(request)).pump.phase).toBe('Calling')
    await expect(face(page)).toContainText('Nozzle lifted')
    await tapCardAtPump(page, 'Good')
    await expect.poll(async () => (await state(request)).pump.phase).toBe('Armed')
    expect((await state(request)).sale.nozzleOut).toBe(true)
    await d.getByRole('button', { name: /^Regular / }).click()
    await d.getByRole('button', { name: 'Insert spout' }).click()
    await d.getByRole('button', { name: /^Squeeze lever/ }).click()
    await expect(face(page)).toContainText('Fueling')
    await d.getByRole('button', { name: 'Hang nozzle' }).click()
    await expect(face(page)).toContainText('Thank you', { timeout: 30_000 })
    expect((await state(request)).sale.hold).toBe('Settled')
  })

  test('a dry tank while idle closes the reader and the counter until the supply is back', async ({ page, request }) => {
    const d = dispenser(page)
    await d.getByRole('button', { name: 'Tank runs dry' }).click()
    await expect(face(page)).toContainText('Out of service')
    await expect(d).toContainText('reader closed')
    await d.getByRole('button', { name: 'Tap' }).click()
    await expect(d.locator('.reader-note')).toContainText(/out of service/i)
    expect((await state(request)).sale.state).toBe('Idle')

    const s = station(page)
    await s.getByRole('button', { name: 'Card at the counter' }).click()
    await expect(s.locator('.text-error', { hasText: /out of service/i }).first()).toBeVisible()
    expect((await state(request)).openHolds).toHaveLength(0)

    await d.getByRole('button', { name: 'Tank refilled' }).click()
    await expect(d).not.toContainText('OUT OF SERVICE')
    await tapCardAtPump(page, 'Good')
  })

  test('a dispenser that dies mid-fuel and comes back clears the link fault once the hold settles', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    await request.post(pumpApi(1, "/dispenser/powerloss"))
    await expect(face(page)).toContainText('Pump offline')
    await expect(dispenser(page)).toContainText('LINK.DISPENSER.LOST')
    await expect.poll(async () => (await state(request)).sale.hold).toBe('Settled')

    await request.post(pumpApi(1, "/dispenser/poweron"))
    await expect(face(page)).toContainText('Pump back in service', { timeout: 30_000 })
    await expect.poll(async () => (await state(request)).dispenser.connected).toBe(true)
    await tapCardAtPump(page, 'Good')
  })
})
