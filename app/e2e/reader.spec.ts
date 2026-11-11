import { test, expect } from '@playwright/test'
import { dispenser, face, open, resetWorld, startFueling, state, station, tapCardAtPump, PIN } from './helpers'

// The full test card numbers, typed here so the scrape does not trust the host's own card list.
const CREDIT_PAN = '4111111111111111'
const DEBIT_PAN = '6011111111111117'
const holdsPin = (text: string) => new RegExp(`(?<![0-9A-Fa-f])${PIN}(?![0-9A-Fa-f])`).test(text)

// The card reader panel: a debit sale shows a KSN, a PIN block and masked digits, and the replay attack is refused at the bank.
test.describe('the card reader', () => {
  test.beforeEach(async ({ page, request }) => {
    await resetWorld(request)
    await open(page)
  })

  test('a debit sale crosses the terminal as ciphertext and a replay of it is refused', async ({ page, request }) => {
    await tapCardAtPump(page, 'DebitGoodPin', 'Debit')
    await expect.poll(async () => (await state(request)).openHolds.length).toBe(1)

    const panel = station(page).getByTestId('reader-panel')
    await expect(panel.getByTestId('reader-ksn')).toHaveText(/^12345678[0-9A-F]{16}$/)
    await expect(panel.getByTestId('terminal-masked')).toContainText('601111******1117')
    await expect(panel.getByTestId('terminal-pinblock')).toHaveText(/^[0-9A-F]{32}$/)
    await expect(panel.getByTestId('bank-opened')).toContainText('PIN block opened and checked')
    await expect(panel).not.toContainText('6011111111111117')

    await panel.getByTestId('reader-attack-replay').click()
    await expect(panel.getByTestId('reader-attack-result')).toContainText('KeyReplayed')
    expect((await state(request)).openHolds).toHaveLength(1)

    await panel.getByTestId('reader-attack-read-memory').click()
    await expect(panel.getByTestId('reader-attack-result')).toContainText('SaleKeyGone')
  })

  // Scrape test A3 over the host's own wire: every HTTP body the host sent the app, every WebSocket frame it pushed,
  // and every request body the app sent except the keypad's (POST .../code carries the typed PIN by design: the app
  // is the keypad hardware, docs/threat-model.md). A full credit sale and a full debit sale, then every reader attack.
  test('no host HTTP body or WebSocket frame holds a card number or the PIN through a credit sale, a debit sale and the attacks', async ({ page, request }) => {
    test.setTimeout(420_000)
    const seen: { what: string; text: string }[] = []
    page.on('response', async r => {
      if (!r.url().includes(':5177/')) return
      try { seen.push({ what: `response ${r.request().method()} ${r.url()}`, text: await r.text() }) } catch { /* a body that is gone */ }
    })
    page.on('request', r => {
      if (r.url().includes(':5177/') && !/\/api\/pump\/\d+\/code$/.test(r.url())) seen.push({ what: `request ${r.method()} ${r.url()}`, text: r.postData() ?? '' })
    })
    page.on('websocket', ws => ws.on('framereceived', f => seen.push({ what: `websocket ${ws.url()}`, text: String(f.payload) })))
    await open(page)

    await tapCardAtPump(page, 'Good')
    await startFueling(page)
    await expect(face(page)).toContainText('Thank you', { timeout: 150_000 })
    // The first driver puts the nozzle back, so the reader takes the next card.
    await dispenser(page).getByRole('button', { name: 'Withdraw spout' }).click()
    await dispenser(page).getByRole('button', { name: 'Hang nozzle' }).click()
    await tapCardAtPump(page, 'DebitGoodPin', 'Debit')
    await startFueling(page)
    await expect(face(page)).toContainText('Thank you', { timeout: 150_000 })
    const panel = station(page).getByTestId('reader-panel')
    for (const attack of ['replay', 'tamper-pin', 'tamper-card', 'rogue-reader', 'read-memory']) {
      await panel.getByTestId(`reader-attack-${attack}`).click()
      await expect(panel.getByTestId('reader-attack-result')).toBeVisible()
    }
    seen.push({ what: 'GET /api/state', text: JSON.stringify(await state(request)) })

    expect(seen.filter(s => s.what.startsWith('websocket')).length).toBeGreaterThan(10)
    expect(seen.some(s => s.what.includes('/reader/attack/'))).toBe(true)
    for (const s of seen) {
      expect(s.text.includes(CREDIT_PAN), `${s.what} holds the credit card number`).toBe(false)
      expect(s.text.includes(DEBIT_PAN), `${s.what} holds the debit card number`).toBe(false)
      expect(holdsPin(s.text), `${s.what} holds the PIN`).toBe(false)
    }
  })
})
