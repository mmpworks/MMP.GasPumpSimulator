// One place that talks to the host. REST for commands, one WebSocket for the stream.
import { reactive, ref } from 'vue'

export const HOST = (window as unknown as { gaspump?: { hostUrl: string } }).gaspump?.hostUrl ?? 'http://localhost:5177'

export interface Notice { headline: string; detail: string; action: string }
export interface Sale {
  saleId: string; state: string; cardName: string | null; grade: string
  ceilingCents: number; cents: number; gallons: number; gpm: number; phase: string; lastMessage: string | null
  hold: string; end: string; notice: Notice; deadlineAt: string | null
  gradeSelected: boolean; nozzleOut: boolean; nozzleSeated: boolean
  tender: 'Credit' | 'Debit' | 'Cash'; cashPrepaidCents: number; changeDueCents: number
}
export interface Site { creditHoldCents: number; debitHoldCents: number; failedPins: number; lockedUntil: string | null; maxPinFailures: number }
export interface Armed { saleId: string; ceilingCents: number; maxGallons: number; clamped: boolean; clampNote: string }
export interface Pending { saleId: string; kind: 'Completion' | 'Reversal'; cents: number }
export interface State {
  strategy: string; pumpId: string; sale: Sale
  pump: {
    phase: string; armed: Armed | null; fault: string | null; totalizerGallons: number
    nozzleLifted: boolean; nozzleSeated: boolean; triggerHeld: boolean; selectedGrade: string | null; poured: boolean
  }
  network: { mode: string; latencyMs: number; lateResponseMs: number; flapFailureRate: number }
  pendingCompletions: { saleId: string; cents: number }[]
  pending: Pending[]
  openHolds: string[]
  site: Site
  cash: { open: { saleId: string; cents: number }[]; changeDue: { saleId: string; cents: number }[] }
  now: string
  limits: { maxCents: number; maxGallons: number; maxMinutes: number; maxGpm: number }
  prices: Record<string, string>
  ledgerOk: boolean
}
export interface LogLine { at: string; source: string; message: string; severity: 'Info' | 'Warn' | 'Error'; props: Record<string, string | null> }
export interface LedgerEntry { seq: number; at: string; kind: string; detail: string; hash: string; prev: string }
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
      case 'log':
        log.push(m); if (log.length > 400) log.shift()
        break
      case 'ledger':
        ledger.push(m); if (ledger.length > 400) ledger.shift()
        break
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
  presentCard: (name: string, tender: 'Credit' | 'Debit' = 'Credit') => post('/api/card', { name, tender }),
  payCash: (cents: number) => post('/api/cash', { cents }),
  changeCollected: () => post('/api/cash/collected'),
  adminSetHold: async (pin: string, tender: 'Credit' | 'Debit', cents: number) =>
    (await (await fetch(`${HOST}/api/admin/limits`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ pin, tender, cents }) })).json()) as { ok: boolean; reason: string },
  selectGrade: (grade: string) => post('/api/grade', { grade }),
  lift: () => post('/api/nozzle/lift'),
  seat: () => post('/api/nozzle/seat'),
  unseat: () => post('/api/nozzle/unseat'),
  squeeze: () => post('/api/trigger/squeeze'),
  release: () => post('/api/trigger/release'),
  hang: () => post('/api/nozzle/hang'),
  cancel: () => post('/api/cancel'),
  reset: () => { flowTrace.splice(0); return post('/api/reset') },
  drain: () => post('/api/drain'),
  network: (mode?: string, latencyMs?: number, flapFailureRate?: number, lateResponseMs?: number) =>
    post('/api/network', { mode, latencyMs, flapFailureRate, lateResponseMs }),
  attack: (kind: string, body?: unknown) => post(`/api/attack/${kind}`, body ?? {}),
  /** Save a copy of the host's log since start. Text for reading, JSON for tools. Lands in the browser's download folder. */
  exportLog: async (format: 'text' | 'json') => {
    const r = await fetch(`${HOST}/api/log/export?format=${format}`)
    const blob = await r.blob()
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `gaspump-log-${new Date().toISOString().replace(/[:.]/g, '-')}.${format === 'json' ? 'json' : 'txt'}`
    a.click()
    URL.revokeObjectURL(a.href)
  },
  verifyLedger: async () => (await fetch(`${HOST}/api/ledger`)).json() as Promise<{ verify: number; totalizer: number }>,
}

export const dollars = (cents: number) => `$${(cents / 100).toFixed(2)}`

/** Seconds left until the sale's armed deadline, or null when none is armed. */
export function secondsLeft(sale: Sale | undefined, now: number): number | null {
  if (!sale?.deadlineAt) return null
  return Math.max(0, Math.round((Date.parse(sale.deadlineAt) - now) / 1000))
}

export const mmss = (s: number) => `${Math.floor(s / 60)}:${(s % 60).toString().padStart(2, '0')}`
