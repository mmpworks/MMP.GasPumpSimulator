import { test, expect } from '@playwright/test'
import { dispenser, face, open, resetWorld, startFueling, state, station, tapCardAtPump } from './helpers'

test.describe('a sale through the wire', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('a card tapped at the pump fuels to the ceiling and the station charges it', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await expect(dispenser(page)).toContainText('tapped at the pump')
    await startFueling(page)
    // $30 of Regular at 10 gal/min is about 51 s of flow plus the taper.
    await expect(face(page)).toContainText('Thank you', { timeout: 120_000 })
    const s = await state(request)
    expect(s.sale.hold).toBe('Settled')
    expect(s.sale.cents).toBe(3000)
    expect(s.sale.source).toBe('PumpTap')
    await expect(station(page)).toContainText('credit: charged')
  })

  test('a second card on an untouched approval cancels the first and starts over', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    const first = (await state(request)).sale.saleId
    // Driver one leaves. Driver two taps a debit card at the same reader.
    await tapCardAtPump(page, 'DebitGoodPin', 'Debit')
    await expect.poll(async () => (await state(request)).sale.saleId).not.toBe(first)
    await expect.poll(async () => (await state(request)).openHolds).toHaveLength(1)
    const s = await state(request)
    expect(s.openHolds[0]).not.toBe(first)
    expect(s.sale.tender).toBe('Debit')
    // The station's ledger says what happened to the first driver's hold.
    await station(page).getByRole('tab', { name: /Ledger/ }).click()
    await expect(station(page).getByText(/sale\.superseded/).first()).toBeVisible()
  })

  test('a second card while the nozzle is in hand is refused', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    const first = (await state(request)).sale.saleId
    const d = dispenser(page)
    await d.getByRole('button', { name: /^Regular / }).click()
    await d.getByRole('button', { name: 'Lift nozzle' }).click()
    await d.getByRole('button', { name: 'Tap' }).click()
    await expect(d.locator('.reader-note')).toContainText('pump in use')
    expect((await state(request)).sale.saleId).toBe(first)
  })
})
