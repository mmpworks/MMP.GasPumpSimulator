// One place that talks to the station host. REST for commands, one WebSocket for the stream.
// Everything the UI shows about the dispenser arrived at the station over the dispenser link;
// the UI never talks to the dispenser process itself. That is the point of the two columns.
import { reactive, ref } from 'vue'

export const HOST = (window as unknown as { gaspump?: { hostUrl: string } }).gaspump?.hostUrl ?? 'http://localhost:5177'

export type Origin = 'dispenser' | 'station'

export interface Notice { headline: string; detail: string; action: string }
export interface Sale {
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
  pid: number; port: number; runId: string; custodyPath: string | null
  estopActive: boolean; estopAt: string | null
  custodyLinesPublished: number; custodyLinesReceived: number; custodyCopyPath: string | null; custodyCopyRunId: string | null
}
export interface DispenserLink {
  link: 'socket' | 'in-process'; connected: boolean; pid: number | null; runId: string | null; custodyPath: string | null
  framesSent: number; framesReceived: number; custodyLines: number; port: number | null
  processRunning: boolean; external: boolean; lastExit: string | null
}
export interface Site { creditHoldCents: number; debitHoldCents: number; failedPins: number; lockedUntil: string | null; maxPinFailures: number }
export interface Recovery {
  kind: 'CleanStart' | 'ClosedOut' | 'Reversed' | 'CompletionResent'; previousRunId: string; saleId: string | null; tender: string; grade: string
  gallons: number; cents: number; unrecordedUpToGallons: number; snapshotAgeSeconds: number; underRecorded: boolean; note: string
}
export interface Armed { saleId: string; ceilingCents: number; maxGallons: number; clamped: boolean; clampNote: string }
export interface Pending { saleId: string; kind: 'Completion' | 'Reversal'; cents: number }
export interface Pump {
  phase: string; armed: Armed | null; fault: string | null; faultCode: string | null; totalizerGallons: number
  nozzleLifted: boolean; spoutInserted: boolean; leverHeld: boolean; selectedGrade: string | null; poured: boolean
  supplyAvailable: boolean; breakawayOpen: boolean; pulserDead: boolean; ledgerIntact: boolean; runId: string; pid: number
  /** Empty codes means in service. Each code names the part that holds the pump out of service until its repair. */
  inService: boolean; serviceCodes: string[]; serviceParts: string[]
}
export interface State {
  strategy: string; pumpId: string; sale: Sale
  station: Station
  dispenser: DispenserLink
  pump: Pump
  network: { mode: string; latencyMs: number; lateResponseMs: number; flapFailureRate: number }
  pendingCompletions: { saleId: string; cents: number }[]
  pending: Pending[]
  openHolds: string[]
  site: Site
  cash: { open: { saleId: string; cents: number }[]; changeDue: { saleId: string; cents: number }[] }
  runId: string
  recovery: Recovery
  custody: { path: string | null; journalPath: string | null }
  now: string
  limits: { maxCents: number; maxGallons: number; maxMinutes: number; maxGpm: number }
  prices: Record<string, string>
  ledgerOk: boolean
}
export interface LogLine { origin: Origin; at: string; source: string; message: string; severity: 'Info' | 'Warn' | 'Error'; props: Record<string, string | null> }
export interface LedgerEntry { origin: Origin; seq: number; at: string; kind: string; detail: string; hash: string; prev: string }
export interface Card { name: string; last4: string; kind: string; expiry: string }

export const state = ref<State | null>(null)
export const log = reactive<LogLine[]>([])
export const ledger = reactive<LedgerEntry[]>([])
export const cards = ref<Card[]>([])
export const connected = ref(false)
export const logFile = ref<{ path: string; directory: string; format: string } | null>(null)
export const flowTrace = reactive<number[]>([])

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
        break
      case 'sale':
        if (state.value) state.value.sale = m
        if (m.state === 'Fueling') { flowTrace.push(m.gpm); if (flowTrace.length > 240) flowTrace.shift() }
        if (m.state === 'ReadyToPump' && m.phase !== 'armed') flowTrace.splice(0)
        break
      case 'pump':
        if (state.value) state.value.pump = m
        break
      case 'log':
        log.push(m); if (log.length > 800) log.shift()
        break
      case 'ledger':
        ledger.push(m); if (ledger.length > 800) ledger.shift()
        break
      case 'run': {
        // The station restarted (power loss or E-stop reset). Its ledger restarts at seq 1; the dispenser's does not.
        const keep = ledger.filter(e => e.origin === 'dispenser')
        ledger.splice(0, ledger.length, ...keep)
        break
      }
      case 'dispenser-run': {
        // The dispenser restarted. Its ledger restarts at seq 1; the station's does not.
        const keep = ledger.filter(e => e.origin === 'station')
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

export const api = {
  /** A card at the pump reader (insert or tap) or at the station counter. 409 means the pump is in use; the body says by whom. */
  presentCard: async (name: string, tender: 'Credit' | 'Debit' = 'Credit', source: CardSource = 'PumpInsert') => {
    const r = await fetch(`${HOST}/api/card`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name, tender, source }) })
    const body = (await r.json()) as { saleId: string; error?: string }
    return { ok: r.ok, ...body }
  },
  /** The keypad: a 4-digit PIN for debit, a 5-digit billing ZIP for credit. */
  enterCode: (code: string) => post('/api/code', { code }),
  payCash: (cents: number) => post('/api/cash', { cents }),
  changeCollected: () => post('/api/cash/collected'),
  adminSetHold: async (pin: string, tender: 'Credit' | 'Debit', cents: number) =>
    (await (await fetch(`${HOST}/api/admin/limits`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ pin, tender, cents }) })).json()) as { ok: boolean; reason: string },
  selectGrade: (grade: string) => post('/api/grade', { grade }),
  lift: () => post('/api/nozzle/lift'),
  insertSpout: () => post('/api/spout/insert'),
  withdrawSpout: () => post('/api/spout/withdraw'),
  squeeze: () => post('/api/lever/squeeze'),
  release: () => post('/api/lever/release'),
  hang: () => post('/api/nozzle/hang'),
  cancel: () => post('/api/cancel'),
  reset: () => { flowTrace.splice(0); return post('/api/reset') },
  drain: () => post('/api/drain'),
  network: (mode?: string, latencyMs?: number, flapFailureRate?: number, lateResponseMs?: number) =>
    post('/api/network', { mode, latencyMs, flapFailureRate, lateResponseMs }),
  attack: (kind: string, body?: unknown) => post(`/api/attack/${kind}`, body ?? {}),
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
  custodyInfo: async () => (await fetch(`${HOST}/api/custody`)).json() as Promise<{ runId: string; path: string | null; journalPath: string | null; ikmPath: string; files: string[]; dispenserPath: string | null; dispenserRunId: string | null }>,
  verifyCustody: async () => (await (await fetch(`${HOST}/api/custody/verify`, { method: 'POST' })).json()) as { ok: boolean; failureAtSeq: number | null; reason: string | null; lines: number; status: string },
  fault: (kind: 'tankout' | 'tankback' | 'pulserdead' | 'pulserfix' | 'driveoff' | 'breakawayreset') => post(`/api/fault/${kind}`),
  /** The operator's pump switch. Closed is a state, never a fault. */
  closePump: (by = 'operator') => post('/api/station/pump/close', { by }),
  openPump: (by = 'operator') => post('/api/station/pump/open', { by }),
  estop: (pressedBy: string) => post('/api/station/estop', { pressedBy }),
  estopReset: async () => (await (await fetch(`${HOST}/api/station/reset`, { method: 'POST' })).json()) as { estopActive: boolean; recovery: Recovery; runId: string },
  verifyStationCopy: async () => (await (await fetch(`${HOST}/api/station/custody/verify`, { method: 'POST' })).json()) as { ok: boolean; failureAtSeq: number | null; reason: string | null; lines: number },
  /** Cuts power to the station. The dispenser process keeps running and is told Stop when the new station connects. */
  powerLoss: async () => (await (await fetch(`${HOST}/api/host/powerloss`, { method: 'POST' })).json()) as { recovery: Recovery; runId: string },
  /** Cuts power to the dispenser. The station sees the link drop and faults the sale with LINK.DISPENSER.LOST. */
  dispenserPowerLoss: () => post('/api/dispenser/powerloss'),
  dispenserPowerOn: () => post('/api/dispenser/poweron'),
  verifyLedger: async () => (await fetch(`${HOST}/api/ledger`)).json() as Promise<{ verify: number; totalizer: number }>,
}

export const dollars = (cents: number) => `$${(cents / 100).toFixed(2)}`
export const basename = (p: string | null | undefined) => (p ? p.split(/[\\/]/).pop() ?? '' : '')

/** Seconds left until the sale's armed deadline, or null when none is armed. */
export function secondsLeft(sale: Sale | undefined, now: number): number | null {
  if (!sale?.deadlineAt) return null
  return Math.max(0, Math.round((Date.parse(sale.deadlineAt) - now) / 1000))
}

export const mmss = (s: number) => `${Math.floor(s / 60)}:${(s % 60).toString().padStart(2, '0')}`
