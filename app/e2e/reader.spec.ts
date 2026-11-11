import { test, expect } from '@playwright/test'
import { open, resetWorld, state, station, tapCardAtPump } from './helpers'

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
})
