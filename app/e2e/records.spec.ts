import { test, expect, type Locator, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { HOST, dispenser, open, pickCard, resetWorld, state, station } from './helpers'

/** Turn the wheel over a panel until it stops moving, the way a person would reach the bottom. */
async function wheelToBottom(page: Page, panel: Locator) {
  const box = (await panel.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  for (let i = 0; i < 12; i++) {
    await page.mouse.wheel(0, 400)
    await page.waitForTimeout(30)
  }
}

async function expectInsideWindow(page: Page, target: Locator) {
  const box = (await target.boundingBox())!
  const height = page.viewportSize()!.height
  expect(box.y + box.height, `bottom ${box.y + box.height} against window ${height}`).toBeLessThanOrEqual(height)
}

test.describe('records panels', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('both columns and their records panels end inside the window', async ({ page }) => {
    for (const side of [dispenser(page), station(page)]) {
      await expectInsideWindow(page, side)
      await expectInsideWindow(page, side.locator('.records-panel'))
    }
  })

  for (const [name, height] of [['a tall window', 1050], ['a short window', 620]] as const) {
    for (const tab of ['Log', 'Ledger', 'Chain']) {
      test(`${tab} tab: the wheel reaches its last line in ${name}`, async ({ page }) => {
        await page.setViewportSize({ width: 1400, height })
        for (const side of [dispenser(page), station(page)]) {
          const card = side.locator('.records-panel')
          await card.getByRole('tab', { name: new RegExp(`^${tab}`) }).click()
          const content = card.locator('.v-card-text > div:visible').first()
          await wheelToBottom(page, card.locator('.v-card-text'))
          const last = content.locator('> *').last()
          await expectInsideWindow(page, last)
          const cardBox = (await card.boundingBox())!
          const lastBox = (await last.boundingBox())!
          expect(lastBox.y + lastBox.height, 'last line inside its panel').toBeLessThanOrEqual(cardBox.y + cardBox.height + 1)
        }
      })
    }
  }
})

test.describe('card type at the pump', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('the card sets the indicator, and the indicator takes no clicks', async ({ page }) => {
    const d = dispenser(page)
    const indicator = d.getByTestId('reader-kind')
    await expect(indicator).toHaveText('Credit')
    await pickCard(page, d, 'DebitGoodPin')
    await expect(indicator).toHaveText('Debit')
    // BadLuhn sits next to the debit cards in the list, so the select has it on screen. It is a credit card.
    await pickCard(page, d, 'BadLuhn')
    await expect(indicator).toHaveText('Credit')
    await expect(d.getByRole('button', { name: 'Credit', exact: true })).toHaveCount(0)
    await expect(d.getByRole('button', { name: 'Debit', exact: true })).toHaveCount(0)
  })

  for (const [button, card, kind] of [['Insert', 'DebitGoodPin', 'Debit'], ['Tap', 'DebitGoodPin', 'Debit'], ['Insert', 'Good', 'Credit'], ['Tap', 'Good', 'Credit']] as const) {
    test(`${button} with ${card} runs as ${kind}`, async ({ page, request }) => {
      const d = dispenser(page)
      await pickCard(page, d, card)
      await d.getByRole('button', { name: button, exact: true }).click()
      await expect.poll(async () => (await state(request)).sale.tender).toBe(kind)
      await expect(d.getByTestId('reader-kind')).toHaveText(kind)
    })
  }
})

test.describe('full paths to the record files', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  /** The panel shows the whole path, it is absolute, and the file is on disk. */
  async function expectRealPath(target: Locator, namePattern: RegExp) {
    const shown = (await target.locator('code').innerText()).trim()
    expect(path.isAbsolute(shown), `${shown} is absolute`).toBe(true)
    expect(path.basename(shown)).toMatch(namePattern)
    expect(fs.existsSync(shown), `${shown} exists`).toBe(true)
    return shown
  }

  test('each tab on each side names a file that exists', async ({ page, request }) => {
    const s = await state(request)
    const hostLog = await (await request.get(`${HOST}/api/logfile`)).json()
    const cases = [
      { side: dispenser(page), key: 'dispenser', log: /^dispenser-PUMP-01-.*\.ndjson$/, chain: /^dispenser-custody-.*\.ndjson$/ },
      { side: station(page), key: 'station', log: /^gaspump-.*\.ndjson$/, chain: /^custody-.*\.ndjson$/ },
    ]
    for (const c of cases) {
      const card = c.side.locator('.records-panel')
      await card.getByRole('tab', { name: /^Log/ }).click()
      const logPath = await expectRealPath(card.getByTestId(`path-log-${c.key}`), c.log)
      if (c.key === 'station') expect(logPath).toBe(hostLog.path)
      if (c.key === 'dispenser') expect(logPath).toBe(s.dispenser.logPath)

      await card.getByRole('tab', { name: /^Ledger/ }).click()
      await expectRealPath(card.getByTestId(`path-ledger-${c.key}`), c.chain)

      await card.getByRole('tab', { name: /^Chain/ }).click()
      await expectRealPath(card.getByTestId(`path-chain-${c.key}`), c.chain)
    }
    await expectRealPath(station(page).locator('.records-panel').getByTestId('path-copy-station'), /^station-copy-P1-.*\.ndjson$/)
  })

  test('the copy buttons put the file path and the folder on the clipboard', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    const row = station(page).locator('.records-panel').getByTestId('path-log-station')
    await station(page).locator('.records-panel').getByRole('tab', { name: /^Log/ }).click()
    const shown = (await row.locator('code').innerText()).trim()
    await row.getByRole('button', { name: 'Copy path' }).click()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(shown)
    await row.getByRole('button', { name: 'Copy folder' }).click()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(path.dirname(shown))
  })
})
