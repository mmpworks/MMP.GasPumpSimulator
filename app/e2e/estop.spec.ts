import { test, expect } from '@playwright/test'
import { dispenser, face, HOST, open, resetWorld, startFueling, state, station, tapCardAtPump } from './helpers'

test.describe('the emergency stop', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('E-stop takes the pump out of service on every surface and the cashier reset restores it with the sale closed', async ({ page, request }) => {
    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    await expect.poll(async () => (await state(request)).sale.cents).toBeGreaterThan(50)

    const s = station(page)
    await s.getByRole('tab', { name: /Power/ }).click()
    await s.getByRole('button', { name: 'E-STOP' }).click()
    await expect(s).toContainText('E-STOP ACTIVE')
    await expect(dispenser(page)).toContainText('STATION.ESTOP')
    await expect(dispenser(page)).toContainText('OUT OF SERVICE')
    const dark = await state(request)
    expect(dark.station.estopActive).toBe(true)
    expect(dark.pump.inService).toBe(false)
    expect(dark.pump.serviceCodes).toContain('STATION.ESTOP')
    await expect.poll(async () => (await state(request)).dispenser.connected, { timeout: 10_000 }).toBe(false)

    await s.getByRole('button', { name: 'Reset at the cashier' }).click()
    await expect(s).not.toContainText('E-STOP ACTIVE')
    await expect.poll(async () => (await state(request)).dispenser.connected, { timeout: 20_000 }).toBe(true)
    await expect.poll(async () => (await state(request)).pump.inService).toBe(true)
    const after = await state(request)
    expect(after.recovery.kind).toBe('ClosedOut')
    expect(after.recovery.unrecordedUpToGallons).toBe(0)
    // The completion is settled from the island's numbers over the simulated bank link, a moment after the pump is back.
    await expect.poll(async () => (await state(request)).openHolds).toHaveLength(0)
    await expect(face(page)).not.toContainText('OUT OF SERVICE')
    await tapCardAtPump(page, 'Good')
  })
})
