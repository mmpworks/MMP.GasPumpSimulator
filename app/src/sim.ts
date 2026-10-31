// One place that talks to the host. REST for commands, one WebSocket for the stream.
import { reactive, ref } from 'vue'

export const HOST = (window as unknown as { gaspump?: { hostUrl: string } }).gaspump?.hostUrl ?? 'http://localhost:5177'

export interface Sale {
  saleId: string; state: string; cardName: string | null; grade: string
  ceilingCents: number; cents: number; gallons: number; gpm: number; phase: string; lastMessage: string | null
}
export interface Armed { saleId: string; ceilingCents: number; maxGallons: number; clamped: boolean; clampNote: string }
export interface State {
  strategy: string; pumpId: string; sale: Sale
  pump: { phase: string; armed: Armed | null; fault: string | null; totalizerGallons: number }
  network: { mode: string; latencyMs: number; flapFailureRate: number }
  pendingCompletions: { saleId: string; cents: number }[]
  limits: { maxCents: number; maxGallons: number; maxMinutes: number; maxGpm: number }
  prices: Record<string, string>
  ledgerOk: boolean
}
export interface LogLine { at: string; source: string; message: string }
export interface LedgerEntry { seq: number; at: string; kind: string; detail: string; hash: string; prev: string }
export interface Card { name: string; last4: string; kind: string; expiry: string }

export const state = ref<State | null>(null)
export const log = reactive<LogLine[]>([])
export const ledger = reactive<LedgerEntry[]>([])
export const cards = ref<Card[]>([])
export const connected = ref(false)
export const flowTrace = reactive<number[]>([])

let socket: WebSocket | null = null

export function connect() {
  fetch(`${HOST}/api/cards`).then(r => r.json()).then(c => (cards.value = c)).catch(() => {})
  socket = new WebSocket(HOST.replace('http', 'ws') + '/ws')
  socket.onopen = () => (connected.value = true)
  socket.onclose = () => { connected.value = false; setTimeout(connect, 1000) }
  socket.onmessage = ev => {
    const m = JSON.parse(ev.data)
    switch (m.type) {
      case 'state':
        state.value = m
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
  presentCard: (name: string, grade: string) => post('/api/card', { name, grade }),
  lift: () => post('/api/nozzle/lift'),
  hang: () => post('/api/nozzle/hang'),
  cancel: () => post('/api/cancel'),
  reset: () => { flowTrace.splice(0); return post('/api/reset') },
  drain: () => post('/api/drain'),
  network: (mode?: string, latencyMs?: number, flapFailureRate?: number) => post('/api/network', { mode, latencyMs, flapFailureRate }),
  attack: (kind: string, body?: unknown) => post(`/api/attack/${kind}`, body ?? {}),
  verifyLedger: async () => (await fetch(`${HOST}/api/ledger`)).json() as Promise<{ verify: number; totalizer: number }>,
}

export const dollars = (cents: number) => `$${(cents / 100).toFixed(2)}`
