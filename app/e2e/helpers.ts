import { expect, type Page, type APIRequestContext } from '@playwright/test'

export const HOST = 'http://localhost:5177'

/** The station's own view of the world, straight from the host, for assertions the screen cannot carry. */
export async function state(request: APIRequestContext) {
  const r = await request.get(`${HOST}/api/state`)
  expect(r.ok()).toBeTruthy()
  return r.json()
}

/** A clean slate between tests: new sale, bank link up, every fault cleared, dispenser powered. */
export async function resetWorld(request: APIRequestContext) {
  await request.post(`${HOST}/api/dispenser/poweron`)
  await expect.poll(async () => (await state(request)).dispenser.connected, { timeout: 15_000 }).toBe(true)
  await request.post(`${HOST}/api/network`, { data: { mode: 'Up', latencyMs: 50 } })
  const s = await state(request)
  if (s.pump.breakawayOpen) await request.post(`${HOST}/api/fault/breakawayreset`)
  if (!s.pump.supplyAvailable) await request.post(`${HOST}/api/fault/tankback`)
  if (s.pump.pulserDead) await request.post(`${HOST}/api/fault/pulserfix`)
  await request.post(`${HOST}/api/nozzle/hang`)
  await request.post(`${HOST}/api/reset`)
  await expect.poll(async () => (await state(request)).sale.state).toBe('Idle')
  await expect.poll(async () => (await state(request)).pump.inService).toBe(true)
  // A reset must leave nothing at the bank. If this fails, a path leaked a hold.
  await expect.poll(async () => (await state(request)).openHolds).toHaveLength(0)
}

export const dispenser = (page: Page) => page.locator('.side.dispenser')
export const station = (page: Page) => page.locator('.side.station')
export const face = (page: Page) => dispenser(page).locator('.notice')
export const appBar = (page: Page) => page.locator('.v-app-bar')

export async function open(page: Page) {
  await page.goto('/')
  await expect(appBar(page).getByText('station connected', { exact: true })).toBeVisible()
}

/** Pick a card in a Vuetify select inside the given column. */
export async function pickCard(page: Page, column: ReturnType<typeof dispenser>, card: string) {
  await column.locator('.v-select .v-field').first().click()
  await page.getByRole('option', { name: new RegExp(`^${card} ·`) }).click()
}

/** What the test bank has on file: billing ZIP for every credit card, PIN for DebitGoodPin. */
export const ZIP = '12345'
export const PIN = '1234'

/** Tap a card at the pump's own reader, answer the keypad, and wait for the approval on the face. */
export async function tapCardAtPump(page: Page, card = 'Good', tender: 'Credit' | 'Debit' = 'Credit', code = tender === 'Debit' ? PIN : ZIP) {
  const d = dispenser(page)
  await pickCard(page, d, card)
  await d.getByRole('button', { name: tender, exact: true }).click()
  await d.getByRole('button', { name: 'Tap' }).click()
  await enterCode(page, code)
  await expect(face(page)).toContainText('Approved for up to')
}

/** The keypad step: the face asks for a PIN or a ZIP, the driver types it and presses Enter. */
export async function enterCode(page: Page, code: string) {
  const d = dispenser(page)
  await expect(face(page)).toContainText(/Enter your \d-digit/)
  await d.locator('.keypad input').fill(code)
  await d.getByRole('button', { name: 'Enter', exact: true }).click()
}

/** Grade, lift, insert, squeeze: the hand sequence, and wait until fuel is moving. */
export async function startFueling(page: Page, grade = 'Regular') {
  const d = dispenser(page)
  await d.getByRole('button', { name: new RegExp(`^${grade} `) }).click()
  await d.getByRole('button', { name: /^Lift nozzle/ }).click()
  await d.getByRole('button', { name: 'Insert spout' }).click()
  await d.getByRole('button', { name: /^Squeeze lever/ }).click()
  await expect(face(page)).toContainText('Fueling')
}

export async function paneWidths(page: Page) {
  return page.locator('.splitpanes--vertical > .splitpanes__pane').evaluateAll(ps => ps.map(p => Math.round(p.getBoundingClientRect().width)))
}

export async function paneStyles(page: Page) {
  return page.locator('.splitpanes--vertical > .splitpanes__pane').evaluateAll(ps => ps.map(p => p.getAttribute('style')))
}

/** A real drag with intermediate moves, which is what splitpanes listens for. */
export async function dragSplitter(page: Page, index: number, dx: number) {
  const splitter = page.locator('.splitpanes--vertical > .splitpanes__splitter').nth(index)
  const box = (await splitter.boundingBox())!
  const x = box.x + box.width / 2, y = box.y + 300
  await page.mouse.move(x, y)
  await page.mouse.down()
  const steps = 12
  for (let i = 1; i <= steps; i++) await page.mouse.move(x + (dx * i) / steps, y)
  await page.mouse.up()
  // splitpanes unbinds its move listeners 100 ms after mouseup; a drag begun inside that window is dropped.
  await page.waitForTimeout(250)
}
