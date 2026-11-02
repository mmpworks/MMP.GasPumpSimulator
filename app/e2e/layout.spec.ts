import { test, expect } from '@playwright/test'
import { dragSplitter, open, paneStyles, paneWidths, resetWorld } from './helpers'

test.describe('panels', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('a column that grew can shrink back past where it started', async ({ page }) => {
    const before = await paneWidths(page)
    await dragSplitter(page, 0, 300)
    const grown = await paneWidths(page)
    expect(grown[0], `grow: ${before} -> ${grown} styles ${await paneStyles(page)}`).toBeGreaterThan(before[0] + 150)

    await dragSplitter(page, 0, -500)
    const shrunk = await paneWidths(page)
    expect(shrunk[0], `shrink: ${before} -> ${grown} -> ${shrunk} styles ${await paneStyles(page)}`).toBeLessThan(before[0] - 100)
    // Nothing was lost: the three panes still fill the row.
    expect(shrunk[0] + shrunk[1] + shrunk[2]).toBeGreaterThan(before[0] + before[1] + before[2] - 40)
  })

  test('both columns name their own process and the strip names the wire', async ({ page }) => {
    await expect(page.locator('.side.dispenser .side-title')).toContainText(/DISPENSER · GasPump\.Dispenser\.exe pid \d+/)
    await expect(page.locator('.side.station .side-title')).toContainText(/STATION · GasPump\.Host\.exe pid \d+/)
    await expect(page.locator('.link-strip')).toContainText('TCP')
    await expect(page.locator('.link-strip')).toContainText(':5178')
  })

  test('the strip shows the envelope: a sequence number climbing, a heartbeat under a few seconds old, no gaps', async ({ page }) => {
    const seq = page.getByTestId('link-seq')
    const beat = page.getByTestId('link-heartbeat')
    await expect(seq).toContainText(/seq$/)                    // no " · N gap" suffix on a healthy wire
    await expect(seq.locator('b')).not.toHaveText('0', { timeout: 10_000 })
    await expect(beat).toContainText('beat')
    await expect(beat.locator('b')).toHaveText(/^[0-3]s$/, { timeout: 10_000 })
    await expect(page.getByTestId('link-open-tx')).toHaveCount(0)
  })
})
