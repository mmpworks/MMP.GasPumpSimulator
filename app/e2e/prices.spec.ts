import { test, expect, type Page } from '@playwright/test'
import { dispenser, face, HOST, open, PIN, resetWorld, startFueling, state, station, tapCardAtPump } from './helpers'

// The price board is the station's to post and the island's to keep. A change goes over the wire as a write, the pump
// answers, and the face shows the price the pump bills by. The totals come back the other way, as a read.
test.describe('prices as a write, totals as a read', () => {
  /** The pump's own arithmetic: mills per gallon times thousandths of a gallon, rounded half up to the cent. */
  const centsFor = (gallons: number, mills: number) => Math.floor((Math.round(gallons * 1000) * mills + 5000) / 10000)

  /** Pour for a moment, then hang up: the sale settles for what moved. Returns the settled sale. */
  async function pourAndHang(page: Page, request: Parameters<typeof state>[0], grade = 'Regular') {
    await startFueling(page, grade)
    await expect.poll(async () => (await state(request)).sale.cents).toBeGreaterThan(100)
    await dispenser(page).getByRole('button', { name: 'Hang nozzle' }).click()
    await expect(face(page)).toContainText('Thank you', { timeout: 30_000 })
    await expect.poll(async () => (await state(request)).sale.hold).toBe('Settled')
    return (await state(request)).sale as { cents: number; gallons: number; grade: string }
  }

  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
    // Every test starts from the built-in board, whatever the last one left on the island.
    for (const [grade, mills] of [['Regular', 3499], ['Plus', 3799], ['Premium', 4099]] as const)
      await request.post(`${HOST}/api/admin/prices`, { data: { pin: PIN, grade, mills } })
    await expect.poll(async () => (await state(request)).prices.Regular).toBe('$3.499')
  })

  test('the back office reprices a grade, the island answers, and the pump face shows the new price', async ({ page, request }) => {
    const s = station(page)
    await s.getByRole('tab', { name: /Administrator/ }).click()
    await s.getByLabel('admin PIN').fill(PIN)
    await s.getByLabel('Regular, $/gal').fill('3.999')
    await s.getByRole('button', { name: 'Set Regular' }).click()
    await expect(s.getByText('Price written: Regular $3.499 -> $3.999')).toBeVisible()

    // The price on the face and on the pump's grade button comes from the island's view, so it proves the write landed and was kept.
    await expect(dispenser(page).getByTestId('face-price')).toHaveText('$3.999')
    await expect(dispenser(page).getByRole('button', { name: /^Regular / })).toContainText('$3.999')
    await expect(s.getByTestId('pump-price-Regular')).toContainText('$3.999')
    await expect.poll(async () => (await state(request)).pump.prices.Regular).toBe(3999)
    expect((await state(request)).pricesInSync).toBe(true)

    // The driver pays the new price.
    await tapCardAtPump(page, 'Good')
    const sale = await pourAndHang(page, request)
    expect(sale.cents).toBe(centsFor(sale.gallons, 3999))
    expect(sale.cents).not.toBe(centsFor(sale.gallons, 3499))
  })

  test('a price change during a delivery waits, the driver pays the posted price, and the next sale pays the new one', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    const r = await request.post(`${HOST}/api/admin/prices`, { data: { pin: PIN, grade: 'Regular', mills: 2000 } })
    expect((await r.json()).ok).toBe(true)
    // The station posted it; the pump holds it until the pour ends, and the panel says so.
    const s = station(page)
    await s.getByRole('tab', { name: /Administrator/ }).click()
    await expect(s.getByTestId('prices-pending')).toBeVisible()
    await expect(dispenser(page).getByTestId('face-price')).toHaveText('$3.499')
    await expect.poll(async () => (await state(request)).sale.cents).toBeGreaterThan(100)
    await dispenser(page).getByRole('button', { name: 'Hang nozzle' }).click()
    await expect(face(page)).toContainText('Thank you', { timeout: 30_000 })
    await expect.poll(async () => (await state(request)).sale.hold).toBe('Settled')
    const first = (await state(request)).sale
    expect(first.cents).toBe(centsFor(first.gallons, 3499))

    // The delivery over, the island applies the board and the station's view follows.
    await expect(s.getByTestId('prices-pending')).toBeHidden()
    await expect.poll(async () => (await state(request)).prices.Regular).toBe('$2.000')
    await tapCardAtPump(page, 'Good')
    await expect(dispenser(page).getByTestId('face-price')).toHaveText('$2.000')
    const second = await pourAndHang(page, request)
    expect(second.cents).toBe(centsFor(second.gallons, 2000))
  })

  test('reading the totals brings back the sealed counter and the per-grade rows', async ({ page, request }) => {
    const before = (await state(request)).pump.totalizerGallons
    await tapCardAtPump(page, 'Good')
    const sale = await pourAndHang(page, request, 'Premium')

    const s = station(page)
    await s.getByRole('tab', { name: /Power/ }).click()
    await s.getByRole('button', { name: 'Read pump totals' }).click()
    await expect(s.getByTestId('totals')).toBeVisible()
    await expect(s.getByTestId('totals')).toContainText(`totalizer ${(before + sale.gallons).toFixed(3)} gal`)
    await expect(s.getByTestId('totals')).toContainText(/Premium .* deliveries/)
    const totals = (await state(request)).totals
    expect(totals.totalizerGallons).toBeCloseTo(before + sale.gallons, 3)
    const premium = totals.byGrade.find((g: { grade: string }) => g.grade === 'Premium')
    expect(premium.deliveries).toBeGreaterThanOrEqual(1)
    expect(premium.cents).toBeGreaterThanOrEqual(sale.cents)
  })
})
