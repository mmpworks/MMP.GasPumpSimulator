<script setup lang="ts">
import { ref } from 'vue'
import { api, ledger } from '../sim'

const verdict = ref<{ ok: boolean; text: string } | null>(null)
async function verify() {
  const r = await api.verifyLedger()
  verdict.value = r.verify === 0
    ? { ok: true, text: `chain intact, totalizer ${r.totalizer.toFixed(3)} gal` }
    : { ok: false, text: `BROKEN at entry ${r.verify}` }
}
const kindClass = (k: string) => k.includes('refused') || k.includes('fault') || k.includes('locked') ? 'kind-bad' : k.includes('ok') || k.includes('open') || k.includes('limits') ? 'kind-ok' : ''
</script>

<template>
  <!-- A plain class, never Vuetify's d-flex: its !important display would defeat the parent's v-show. -->
  <div class="ledger-root">
    <div class="row-wrap mb-2">
      <v-btn prepend-icon="mdi-link-lock" @click="verify">Verify chain</v-btn>
      <v-chip v-if="verdict" :color="verdict.ok ? 'success' : 'error'">{{ verdict.text }}</v-chip>
      <span v-else class="muted">every entry carries the hash of the one before it</span>
    </div>
    <div class="mono-list">
      <div v-for="e in [...ledger].reverse()" :key="e.seq" class="line ledger">
        <span class="src">#{{ e.seq }}</span>
        <span><span :class="kindClass(e.kind)">{{ e.kind }}</span> {{ e.detail }} <span class="hash">{{ e.prev }} → {{ e.hash }}</span></span>
      </div>
    </div>
  </div>
</template>
