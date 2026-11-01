<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import { api, ledger, state } from '../sim'

const verdict = ref<{ ok: boolean; text: string } | null>(null)
async function verify() {
  const r = await api.verifyLedger()
  verdict.value = r.verify === 0
    ? { ok: true, text: `chain intact, totalizer ${r.totalizer.toFixed(3)} gal` }
    : { ok: false, text: `BROKEN at entry ${r.verify}` }
}

// The on-disk custody file, sealed by TesseraSeal. The in-memory ledger above is this run's view; the file is the evidence.
const custody = ref<{ runId: string; path: string | null; files: string[] } | null>(null)
const custodyVerdict = ref<{ ok: boolean; text: string } | null>(null)
async function loadCustody() { try { custody.value = await api.custodyInfo() } catch { /* host not up yet */ } }
async function verifyCustody() {
  const r = await api.verifyCustody()
  custodyVerdict.value = { ok: r.ok, text: r.ok ? `Status: PASS · ${r.lines} lines` : `Status: FAIL · ${r.reason}` }
}
onMounted(loadCustody)
watch(() => state.value?.runId, loadCustody)

// The station server's copy of the chain, received over the custody stream line by line.
const stationVerdict = ref<{ ok: boolean; text: string } | null>(null)
async function verifyStation() {
  const r = await api.verifyStationCopy()
  stationVerdict.value = { ok: r.ok, text: r.ok ? `Status: PASS · ${r.lines} lines received` : `Status: FAIL · ${r.reason}` }
}

const kindClass = (k: string) => k.includes('refused') || k.includes('fault') || k.includes('locked') || k.startsWith('recovery') ? 'kind-bad' : k.includes('ok') || k.includes('open') || k.includes('limits') ? 'kind-ok' : ''
const basename = (p: string | null) => p ? p.split(/[\\/]/).pop() : ''
</script>

<template>
  <div class="ledger-root">
    <div class="row-wrap mb-2">
      <v-btn prepend-icon="mdi-link-lock" @click="verify">Verify chain</v-btn>
      <v-chip v-if="verdict" :color="verdict.ok ? 'success' : 'error'">{{ verdict.text }}</v-chip>
      <span v-else class="muted">every entry carries the hash of the one before it</span>
    </div>
    <div class="row-wrap mb-2">
      <v-btn prepend-icon="mdi-certificate-outline" @click="verifyCustody">Verify custody file</v-btn>
      <v-chip v-if="custodyVerdict" :color="custodyVerdict.ok ? 'success' : 'error'">{{ custodyVerdict.text }}</v-chip>
      <span v-else-if="custody" class="muted" :title="custody.path ?? ''">TesseraSeal HMAC chain · {{ basename(custody.path) }} · {{ custody.files.length }} run file{{ custody.files.length === 1 ? '' : 's' }} on disk</span>
    </div>
    <div class="row-wrap mb-2">
      <v-btn prepend-icon="mdi-server-network" @click="verifyStation">Verify station copy</v-btn>
      <v-chip v-if="stationVerdict" :color="stationVerdict.ok ? 'success' : 'error'">{{ stationVerdict.text }}</v-chip>
      <span v-else class="muted">dispenser published {{ state?.station.custodyLinesPublished ?? 0 }} · station received {{ state?.station.custodyLinesReceived ?? 0 }} · also on ws://localhost:5177/ws/custody</span>
    </div>
    <div class="mono-list">
      <div v-for="e in [...ledger].reverse()" :key="e.seq" class="line ledger">
        <span class="src">#{{ e.seq }}</span>
        <span><span :class="kindClass(e.kind)">{{ e.kind }}</span> {{ e.detail }} <span class="hash">{{ e.prev }} → {{ e.hash }}</span></span>
      </div>
    </div>
  </div>
</template>
