// One place that talks to the station host. REST for commands, one WebSocket for the stream.
// Everything the UI shows about a dispenser arrived at the station over that pump's link;
// the UI never talks to a dispenser process itself. That is the point of the two columns.
// The station drives several pumps; the window shows one at a time, on a tab, and `activePump` says which.
import { computed, reactive, ref } from 'vue'

export const HOST = (window as unknown as { gaspump?: { hostUrl: string } }).gaspump?.hostUrl ?? 'http://localhost:5177'

export type Origin = 'dispenser' | 'station'

export interface Notice { headline: string; detail: string; action: string }
export interface Sale {
  /** The pump the sale is on, read off the sale id (P1-S0001 is pump 1). */
  pump: number | null
  saleId: string; state: string; cardName: string | null; grade: string
  ceilingCents: number; cents: number; gallons: number; gpm: number; phase: string; lastMessage: string | null
  hold: string; end: string; notice: Notice; deadlineAt: string | null
  gradeSelected: boolean; nozzleOut: boolean; spoutInserted: boolean
  tender: 'Credit' | 'Debit' | 'Cash'; cashPrepaidCents: number; changeDueCents: number
  source: 'PumpInsert' | 'PumpTap' | 'Station'
  faultCode: string | null; faultPart: string | null
}
export type CardSource = 'PumpInsert' | 'PumpTap' | 'Station'
export interface Station {
  /** The sign over the door and the colour theme the host was started with (GASPUMP_STATION, GASPUMP_THEME). */
  name: string; theme: string
  pid: number; port: number; runId: string; custodyPath: string | null
  estopActive: boolean; estopAt: string | null
}
export interface DispenserLink {
  link: 'socket' | 'in-process'; connected: boolean; pid: number | null; runId: string | null; custodyPath: string | null; logPath: string | null
  framesSent: number; framesReceived: number; custodyLines: number; port: number | null
  /** The envelope's view of the wire: sequence numbers each way, gaps asked for, duplicates dropped, the peer's last heartbeat. */
  lastSeqSent: number; lastSeqReceived: number; gaps: number; duplicates: number
  heartbeatAt: string | null; peerStatus: string | null; peerAlive: boolean
  processRunning: boolean; external: boolean; lastExit: string | null
}
/** A delivery the island still holds for the station: Payable until the station locks it, Cleared once the money landed. */
export interface OpenTransaction { txSeq: number; saleId: string; grade: string; gallons: number; cents: number; state: 'Payable' | 'Locked' | 'Cleared'; lockedBy: string | null; endReason: string }
export interface Site { creditHoldCents: number; debitHoldCents: number; failedPins: number; lockedUntil: string | null; maxPinFailures: number }
export interface Recovery {
  kind: 'CleanStart' | 'ClosedOut' | 'Reversed' | 'CompletionResent'; previousRunId: string; saleId: string | null; tender: string; grade: string
  gallons: number; cents: number; unrecordedUpToGallons: number; snapshotAgeSeconds: number; underRecorded: boolean; note: string
}
export interface Armed { saleId: string; ceilingCents: number; maxGallons: number; clamped: boolean; clampNote: string }
export interface Pending { saleId: string; kind: 'Completion' | 'Reversal'; cents: number }
export interface Pump {
  pump: number | null; pumpId: string
  phase: string; armed: Armed | null; fault: string | null; faultCode: string | null; totalizerGallons: number
  nozzleLifted: boolean; spoutInserted: boolean; leverHeld: boolean; selectedGrade: string | null; poured: boolean
  supplyAvailable: boolean; breakawayOpen: boolean; pulserDead: boolean; ledgerIntact: boolean; runId: string; pid: number
  /** Empty codes means in service. Each code names the part that holds the pump out of service until its repair. */
  inService: boolean; serviceCodes: string[]; serviceParts: string[]
  openTransactions: OpenTransaction[]
  /** The board the pump bills by, in mills per gallon ($3.499 is 3499). Null until a dispenser has answered. */
  prices: Record<string, number> | null
}
/** The island's counters as last read: the sealed totalizer live, the grade rows by finished delivery. */
export interface GradeTotal { grade: string; gallons: number; cents: number; deliveries: number }
export interface Totals { totalizerGallons: number; lastTxSeq: number; readAt: string; byGrade: GradeTotal[] }
/** The station's copy of one pump's chain. */
export interface ChainCopy { path: string | null; linesReceived: number; runId: string | null; linesPublished: number }
/** One fuelling point: everything the station knows about one pump. */
export interface PumpEntry {
  number: number; pumpId: string; node: string
  sale: Sale; pump: Pump; dispenser: DispenserLink; copy: ChainCopy; recovery: Recovery; totals: Totals | null
  /** What the pump bills by, as the face shows it. The station's board stands in until a dispenser has answered. */
  prices: Record<string, string>
  /** False while the island still bills by an older board: a write waiting for a delivery to end, refused, or unanswered. */
  pricesInSync: boolean
}
export interface State {
  strategy: string; pumpCount: number
  station: Station
  pumps: PumpEntry[]
  network: { mode: string; latencyMs: number; lateResponseMs: number; flapFailureRate: number }
  pendingCompletions: { saleId: string; cents: number }[]
  pending: Pending[]
  openHolds: string[]
  site: Site
  cash: { open: { saleId: string; cents: number }[]; changeDue: { saleId: string; cents: number }[] }
  runId: string
  custody: { path: string | null; journalPath: string | null }
  now: string
  limits: { maxCents: number; maxGallons: number; maxMinutes: number; maxGpm: number }
  /** The station's posted board in mills, for the admin fields. */
  priceBoard: Record<string, number>
  ledgerOk: boolean
}
/** `pump` is the pump a record is about; null is the site itself (admin, network, E-stop), shown on every tab. */
export interface LogLine { origin: Origin; pump: number | null; at: string; source: string; message: string; severity: 'Info' | 'Warn' | 'Error'; props: Record<string, string | null> }
export interface LedgerEntry { origin: Origin; pump: number | null; seq: number; at: string; kind: string; detail: string; hash: string; prev: string }
export interface Card { name: string; last4: string; kind: string; expiry: string }

export const state = ref<State | null>(null)
export const log = reactive<LogLine[]>([])
export const ledger = reactive<LedgerEntry[]>([])
export const cards = ref<Card[]>([])
export const connected = ref(false)
export const logFile = ref<{ path: string; directory: string; format: string } | null>(null)
/** Flow-rate traces, one per pump, last 240 ticks each. */
export const flowTraces = reactive<Record<number, number[]>>({})

/** The pump whose tab is open. Every panel reads its pump through `pumpState`, and `api` sends to it unless told otherwise. */
export const activePump = ref(1)
try { const saved = Number(localStorage.getItem('gaspump.active-pump')); if (saved >= 1) activePump.value = saved } catch { /* private window */ }
export function selectPump(n: number) {
  activePump.value = n
  try { localStorage.setItem('gaspump.active-pump', String(n)) } catch { /* ignore */ }
}
export const pumpEntry = (n: number) => state.value?.pumps.find(p => p.number === n)
/** The active pump's entry, or undefined until the first state lands. */
export const pumpState = computed(() => pumpEntry(activePump.value))
export const flowTrace = computed(() => flowTraces[activePump.value] ?? [])

// Host clock minus browser clock, so a countdown reads against the simulation's own time.
let clockSkewMs = 0
export const nowMs = ref(Date.now())
setInterval(() => (nowMs.value = Date.now() + clockSkewMs), 250)

let socket: WebSocket | null = null

export function connect() {
  fetch(`${HOST}/api/cards`).then(r => r.json()).then(c => (cards.value = c)).catch(() => {})
  fetch(`${HOST}/api/logfile`).then(r => r.json()).then(f => (logFile.value = f)).catch(() => {})
  socket = new WebSocket(HOST.replace('http', 'ws') + '/ws')
  socket.onopen = () => (connected.value = true)
  socket.onclose = () => { connected.value = false; setTimeout(connect, 1000) }
  socket.onmessage = ev => {
    const m = JSON.parse(ev.data)
    switch (m.type) {
      case 'state':
        state.value = m
        if (m.now) clockSkewMs = Date.parse(m.now) - Date.now()
        if (state.value && !state.value.pumps.some(p => p.number === activePump.value)) selectPump(state.value.pumps[0]?.number ?? 1)
        break
      case 'sale': {
        const entry = pumpEntry(m.pump)
        if (entry) entry.sale = m
        const trace = (flowTraces[m.pump] ??= [])
        if (m.state === 'Fueling') { trace.push(m.gpm); if (trace.length > 240) trace.shift() }
        if (m.state === 'ReadyToPump' && m.phase !== 'armed') trace.splice(0)
        break
      }
      case 'pump': {
        const entry = pumpEntry(m.pump)
        if (entry) entry.pump = m
        break
      }
      case 'log':
        log.push(m); if (log.length > 1600) log.shift()
        break
      case 'ledger':
        ledger.push(m); if (ledger.length > 1600) ledger.shift()
        break
      case 'run': {
        // The station restarted (power loss or E-stop reset). Its ledger restarts at seq 1; the dispensers' do not.
        const keep = ledger.filter(e => e.origin === 'dispenser')
        ledger.splice(0, ledger.length, ...keep)
        break
      }
      case 'dispenser-run': {
        // One dispenser restarted. Its ledger restarts at seq 1; the station's and the other pumps' do not.
        const keep = ledger.filter(e => !(e.origin === 'dispenser' && e.pump === m.pump))
        ledger.splice(0, ledger.length, ...keep)
        break
      }
    }
  }
}

async function post(path: string, body?: unknown) {
  await fetch(`${HOST}${path}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

/** The route for one pump: /api/pump/{n}/... The active tab's pump unless a number is given. */
const at = (path: string, pump?: number) => `/api/pump/${pump ?? activePump.value}${path}`

export const api = {
  /** A card at the pump reader (insert or tap) or at the station counter. 409 means the pump is in use; the body says by whom. */
  presentCard: async (name: string, tender: 'Credit' | 'Debit' = 'Credit', source: CardSource = 'PumpInsert', pump?: number) => {
    const r = await fetch(`${HOST}${at('/card', pump)}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name, tender, source }) })
    const body = (await r.json()) as { saleId: string; error?: string }
    return { ok: r.ok, ...body }
  },
  /** The keypad: a 4-digit PIN for debit, a 5-digit billing ZIP for credit. */
  enterCode: (code: string, pump?: number) => post(at('/code', pump), { code }),
  payCash: (cents: number, pump?: number) => post(at('/cash', pump), { cents }),
  changeCollected: (pump?: number) => post(at('/cash/collected', pump)),
  adminSetHold: async (pin: string, tender: 'Credit' | 'Debit', cents: number) =>
    (await (await fetch(`${HOST}/api/admin/limits`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ pin, tender, cents }) })).json()) as { ok: boolean; reason: string },
  /** A posted price in mills per gallon. The station writes the whole board to every island, and each answers. */
  adminSetPrice: async (pin: string, grade: string, mills: number) =>
    (await (await fetch(`${HOST}/api/admin/prices`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ pin, grade, mills }) })).json()) as { ok: boolean; reason: string },
  /** The inspector's close-out: ask this island for its counters. The answer lands in the pump's `totals`. */
  readTotals: (pump?: number) => post(at('/totals', pump)),
  selectGrade: (grade: string, pump?: number) => post(at('/grade', pump), { grade }),
  lift: (pump?: number) => post(at('/nozzle/lift', pump)),
  insertSpout: (pump?: number) => post(at('/spout/insert', pump)),
  withdrawSpout: (pump?: number) => post(at('/spout/withdraw', pump)),
  squeeze: (pump?: number) => post(at('/lever/squeeze', pump)),
  release: (pump?: number) => post(at('/lever/release', pump)),
  hang: (pump?: number) => post(at('/nozzle/hang', pump)),
  cancel: (pump?: number) => post(at('/cancel', pump)),
  reset: (pump?: number) => { (flowTraces[pump ?? activePump.value] ??= []).splice(0); return post(at('/reset', pump)) },
  drain: () => post('/api/drain'),
  network: (mode?: string, latencyMs?: number, flapFailureRate?: number, lateResponseMs?: number) =>
    post('/api/network', { mode, latencyMs, flapFailureRate, lateResponseMs }),
  /** An attack on this pump's authorization path: replay, tamper, overlimit, notoken, spoof. */
  attack: (kind: string, body?: unknown, pump?: number) => post(at(`/attack/${kind}`, pump), body ?? {}),
  /** The one attack on the site itself. */
  attackAdminPin: () => post('/api/attack/pinbrute'),
  /** Save a copy of the station's log since start. Text for reading, JSON for tools. Lands in the browser's download folder. */
  exportLog: async (format: 'text' | 'json') => {
    const r = await fetch(`${HOST}/api/log/export?format=${format}`)
    const blob = await r.blob()
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `gaspump-log-${new Date().toISOString().replace(/[:.]/g, '-')}.${format === 'json' ? 'json' : 'txt'}`
    a.click()
    URL.revokeObjectURL(a.href)
  },
  custodyInfo: async () => (await fetch(`${HOST}/api/custody`)).json() as Promise<{ runId: string; path: string | null; journalPath: string | null; ikmPath: string; files: string[]; dispensers: { pump: number; path: string | null; runId: string | null }[] }>,
  verifyCustody: async () => (await (await fetch(`${HOST}/api/custody/verify`, { method: 'POST' })).json()) as { ok: boolean; failureAtSeq: number | null; reason: string | null; lines: number; verdict: string; status: string },
  /** A fault on one island: the pulser or the breakaway. */
  fault: (kind: 'pulserdead' | 'pulserfix' | 'driveoff' | 'breakawayreset', pump?: number) => post(at(`/fault/${kind}`, pump)),
  /** The tank gauge. One tank feeds every pump, so this reaches every island. */
  tank: (kind: 'tankout' | 'tankback') => post(`/api/fault/${kind}`),
  /** The operator's pump switch. Closed is a state, never a fault. */
  closePump: (by = 'operator', pump?: number) => post(at('/switch/close', pump), { by }),
  openPump: (by = 'operator', pump?: number) => post(at('/switch/open', pump), { by }),
  estop: (pressedBy: string) => post('/api/station/estop', { pressedBy }),
  estopReset: async () => (await (await fetch(`${HOST}/api/station/reset`, { method: 'POST' })).json()) as { estopActive: boolean; recoveries: Recovery[]; runId: string },
  verifyStationCopy: async (pump?: number) => (await (await fetch(`${HOST}/api/station/custody/verify?pump=${pump ?? activePump.value}`, { method: 'POST' })).json()) as { ok: boolean; failureAtSeq: number | null; reason: string | null; lines: number; verdict: string },
  /** Cuts power to the station. The dispenser processes keep running and are told Stop when the new station connects. */
  powerLoss: async () => (await (await fetch(`${HOST}/api/host/powerloss`, { method: 'POST' })).json()) as { recoveries: Recovery[]; runId: string },
  /** Cuts power to this pump's dispenser. The station sees the link drop and faults the sale with LINK.DISPENSER.LOST. */
  dispenserPowerLoss: (pump?: number) => post(at('/dispenser/powerloss', pump)),
  dispenserPowerOn: (pump?: number) => post(at('/dispenser/poweron', pump)),
  verifyLedger: async () => (await fetch(`${HOST}/api/ledger`)).json() as Promise<{ verify: number; totalizers: { pump: number; gallons: number }[] }>,
}

export const dollars = (cents: number) => `$${(cents / 100).toFixed(2)}`
export const basename = (p: string | null | undefined) => (p ? p.split(/[\\/]/).pop() ?? '' : '')

/** Seconds left until the sale's armed deadline, or null when none is armed. */
export function secondsLeft(sale: Sale | undefined, now: number): number | null {
  if (!sale?.deadlineAt) return null
  return Math.max(0, Math.round((Date.parse(sale.deadlineAt) - now) / 1000))
}

export const mmss = (s: number) => `${Math.floor(s / 60)}:${(s % 60).toString().padStart(2, '0')}`
