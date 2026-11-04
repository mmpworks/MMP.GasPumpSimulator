import { expect, type Page, type APIRequestContext } from '@playwright/test'

export const HOST = 'http://localhost:5177'

/** The pump-scoped route for pump n: /api/pump/{n}/... */
export const pumpApi = (n: number, path: string) => `${HOST}/api/pump/${n}${path}`

/** The whole station as the host reports it: site-wide fields plus one entry per pump under `pumps`. */
export async function stationState(request: APIRequestContext) {
  const r = await request.get(`${HOST}/api/state`)
  expect(r.ok()).toBeTruthy()
  return r.json()
}

/**
 * One pump's view, flattened for the specs that were written when there was one pump: the site-wide fields,
 * then pump n's sale, pump, dispenser, recovery, totals, prices, and pricesInSync on top. Pump 1 by default.
 */
export async function state(request: APIRequestContext, pump = 1) {
  const s = await stationState(request)
  return { ...s, ...s.pumps[pump - 1] }
}

/** A clean slate for one pump: new sale, every fault cleared, dispenser powered, nothing left at the bank. */
export async function resetPump(request: APIRequestContext, n: number) {
  await request.post(pumpApi(n, '/dispenser/poweron'))
  await expect.poll(async () => (await state(request, n)).dispenser.connected, { timeout: 15_000 }).toBe(true)
  const s = await state(request, n)
  if (s.pump.breakawayOpen) await request.post(pumpApi(n, '/fault/breakawayreset'))
  if (!s.pump.supplyAvailable) await request.post(`${HOST}/api/fault/tankback`)
  if (s.pump.pulserDead) await request.post(pumpApi(n, '/fault/pulserfix'))
  if (s.pump.phase === 'Closed') await request.post(pumpApi(n, '/switch/open'))
  await request.post(pumpApi(n, '/nozzle/hang'))
  await request.post(pumpApi(n, '/reset'))
  await expect.poll(async () => (await state(request, n)).sale.state).toBe('Idle')
  await expect.poll(async () => (await state(request, n)).pump.inService).toBe(true)
}

/** A clean slate between tests: both pumps reset, bank link up. A reset must leave nothing at the bank; if this fails, a path leaked a hold. */
export async function resetWorld(request: APIRequestContext) {
  await request.post(`${HOST}/api/network`, { data: { mode: 'Up', latencyMs: 50 } })
  const s = await stationState(request)
  for (const p of s.pumps as { number: number }[]) await resetPump(request, p.number)
  await expect.poll(async () => (await stationState(request)).openHolds).toHaveLength(0)
}

/**
 * The open pump tab's content, or pump n's when a number is given. Only the open tab is on the page, so a
 * locator for another pump finds nothing until `selectPump` switches to it. The specs written for one pump
 * open on pump 1 and never switch.
 */
export const pumpTab = (page: Page, n?: number) => (n === undefined ? page.locator('.pump-view') : page.locator(`.pump-view[data-pump="${n}"]`))
export const dispenser = (page: Page, n?: number) => pumpTab(page, n).locator('.side.dispenser')
export const station = (page: Page, n?: number) => pumpTab(page, n).locator('.side.station')
export const face = (page: Page, n?: number) => dispenser(page, n).locator('.notice')
export const appBar = (page: Page) => page.locator('.v-app-bar')

export async function open(page: Page) {
  await page.goto('/')
  await expect(appBar(page).getByText('station connected', { exact: true })).toBeVisible()
  await selectPump(page, 1)
}

/** Switch the window to pump n's tab and wait until its content is on screen. */
export async function selectPump(page: Page, n: number) {
  await page.getByRole('tab', { name: new RegExp(`^Pump ${n}\\b`) }).click()
  await expect(pumpTab(page, n)).toBeVisible()
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
  await expect(d.getByTestId('reader-kind')).toHaveText(tender)
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
