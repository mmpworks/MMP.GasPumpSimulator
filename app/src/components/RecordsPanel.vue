<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { api, log, ledger, state, basename, type Origin, pumpState, activePump } from '../sim'

// One side's records: its log, its ledger, its chain. The station host stamps an origin on every record.
// Over the socket the origin is the process that sent the record. In-process, it is read off the record.
const props = defineProps<{ origin: Origin }>()
const isDispenser = computed(() => props.origin === 'dispenser')

type Tab = 'log' | 'ledger' | 'chain'
const tab = ref<Tab>('log')
const key = `gaspump.records.${props.origin}`
try { const saved = localStorage.getItem(key) as Tab | null; if (saved) tab.value = saved } catch { /* private window */ }
watch(tab, t => { try { localStorage.setItem(key, t) } catch { /* ignore */ } })

const filter = ref('')
const time = (s: string) => new Date(s).toLocaleTimeString(undefined, { hour12: false }) + '.' + String(new Date(s).getMilliseconds()).padStart(3, '0')
// This side's records for the pump on the open tab. A record about the site itself (admin, network, E-stop) has no pump and shows on every tab.
const forThisPump = (pump: number | null) => pump === null || pump === activePump.value
const myLog = computed(() => log.filter(l => l.origin === props.origin && forThisPump(l.pump)))
const shownLog = computed(() => {
  const f = filter.value.trim().toLowerCase()
  const rows = f ? myLog.value.filter(l => l.source.includes(f) || l.message.toLowerCase().includes(f)) : myLog.value
  return [...rows].reverse()
})
const myLedger = computed(() => [...ledger.filter(e => e.origin === props.origin && forThisPump(e.pump))].reverse())
const propsOf = (p: Record<string, string | null> | undefined) => Object.entries(p ?? {}).map(([k, v]) => `${k} = ${v}`).join('\n')
const kindClass = (k: string) => k.includes('refused') || k.includes('fault') || k.includes('locked') || k.startsWith('recovery') || k.includes('down') || k.includes('estop') || k.includes('poweroff') ? 'kind-bad' : k.includes('ok') || k.includes('open') || k.includes('limits') || k.includes('.up') || k.includes('start') ? 'kind-ok' : ''

// Chain: each side has its own file. The station also holds a copy of the dispenser's, received over the link.
const ledgerIntact = computed(() => isDispenser.value ? pumpState.value?.pump.ledgerIntact : state.value?.ledgerOk)
const chainPath = computed(() => isDispenser.value ? pumpState.value?.dispenser.custodyPath : state.value?.station.custodyPath)
const custody = ref<{ files: string[] } | null>(null)
async function loadCustody() { try { custody.value = await api.custodyInfo() } catch { /* host not up yet */ } }
onMounted(loadCustody)
watch(() => [state.value?.runId, pumpState.value?.dispenser.runId], loadCustody)

type Verdict = { color: string; text: string }
// A build without the chain answers NOT CHECKED, which is neither a pass nor a tamper alarm.
function verdictOf(r: { ok: boolean; verdict: string; reason: string | null }, passed: string): Verdict {
  const color = r.ok ? 'success' : r.verdict === 'NOT CHECKED' ? 'warning' : 'error'
  return { color, text: `Status: ${r.verdict} · ${r.ok ? passed : r.reason}` }
}
const ownVerdict = ref<Verdict | null>(null)
async function verifyOwn() {
  const r = await api.verifyCustody()
  ownVerdict.value = verdictOf(r, `${r.lines} lines`)
}
const copyVerdict = ref<Verdict | null>(null)
async function verifyCopy() {
  const r = await api.verifyStationCopy()
  copyVerdict.value = verdictOf(r, `${r.lines} lines received`)
}
</script>

<template>
  <v-card class="pane-card fixed">
    <v-tabs v-model="tab" density="compact" :color="isDispenser ? 'warning' : 'info'" grow>
      <v-tab value="log" prepend-icon="mdi-text-long" class="text-none">Log <span class="muted ml-2">{{ myLog.length }}</span></v-tab>
      <v-tab value="ledger" prepend-icon="mdi-link-variant" class="text-none">Ledger <v-chip class="ml-2" size="x-small" :color="ledgerIntact === false ? 'error' : 'success'" variant="flat">{{ ledgerIntact === false ? 'BROKEN' : 'intact' }}</v-chip></v-tab>
      <v-tab value="chain" prepend-icon="mdi-certificate-outline" class="text-none">Chain</v-tab>
    </v-tabs>
    <v-divider />
    <v-card-text>
      <div v-show="tab === 'log'" class="ledger-root">
        <v-text-field v-model="filter" :placeholder="isDispenser ? 'filter: pump, attack, dispenser, text' : 'filter: sale, payment, admin, station, link, text'" prepend-inner-icon="mdi-magnify" clearable density="compact" class="mb-1 flex-grow-0" />
        <div class="mono-list">
          <div v-for="(l, i) in shownLog" :key="shownLog.length - i" class="line" :class="'sev-' + l.severity" :title="propsOf(l.props)">
            <span class="muted">{{ time(l.at) }}</span>
            <span class="src" :class="l.source">{{ l.source }}</span>
            <span>{{ l.message }}</span>
          </div>
        </div>
      </div>
      <div v-show="tab === 'ledger'" class="ledger-root">
        <div class="muted mb-1">{{ isDispenser ? 'written by the dispenser: valve, nozzle, grade, arm, faults, totalizer' : 'written by the station: sale open and close, payment, admin, recovery, E-stop, link' }} · hash-chained, every entry carries the hash of the one before</div>
        <div class="mono-list">
          <div v-for="e in myLedger" :key="e.seq" class="line ledger">
            <span class="src">#{{ e.seq }}</span>
            <span><span :class="kindClass(e.kind)">{{ e.kind }}</span> {{ e.detail }} <span class="hash">{{ e.prev }} → {{ e.hash }}</span></span>
          </div>
        </div>
      </div>
      <div v-show="tab === 'chain'">
        <div class="kv mb-2">
          <b>file</b><span :title="chainPath ?? ''">{{ basename(chainPath) || '-' }}</span>
          <b>lines</b><span>{{ isDispenser ? (pumpState?.dispenser.custodyLines ?? 0) + ' published to the station' : '' }}<template v-if="!isDispenser">this run's own chain</template></span>
          <b>sealed by</b><span>TesseraSeal HMAC chain (Herald.Compliance ImmutableAuditFileSink), site key <code>custody.ikm</code></span>
        </div>
        <template v-if="isDispenser">
          <p class="muted">The dispenser writes this file and streams every line to the station as it lands. The station keeps a copy and verifies the copy; the dispenser never has to be asked. Verify the copy from the station column.</p>
        </template>
        <template v-else>
          <div class="row-wrap mb-2">
            <v-btn size="small" prepend-icon="mdi-certificate-outline" @click="verifyOwn">Verify station chain</v-btn>
            <v-chip v-if="ownVerdict" size="small" :color="ownVerdict.color">{{ ownVerdict.text }}</v-chip>
          </div>
          <div class="row-wrap mb-2">
            <v-btn size="small" prepend-icon="mdi-server-network" @click="verifyCopy">Verify copy of dispenser chain</v-btn>
            <v-chip v-if="copyVerdict" size="small" :color="copyVerdict.color">{{ copyVerdict.text }}</v-chip>
            <span v-else class="muted">{{ pumpState?.copy.linesReceived ?? 0 }} lines received for pump {{ activePump }} · {{ basename(pumpState?.copy.path) || '-' }} · every pump also on ws://localhost:{{ state?.station.port ?? 5177 }}/ws/custody</span>
          </div>
        </template>
        <div v-if="custody" class="muted">{{ custody.files.length }} chain file{{ custody.files.length === 1 ? '' : 's' }} on disk from both sides</div>
      </div>
    </v-card-text>
  </v-card>
</template>
