<script setup lang="ts">
import { ref } from 'vue'
import { api, ledger } from '../sim'

const verdict = ref<string | null>(null)
async function verify() {
  const r = await api.verifyLedger()
  verdict.value = r.verify === 0 ? `chain intact, totalizer ${r.totalizer.toFixed(3)} gal` : `BROKEN at entry ${r.verify}`
}
</script>

<template>
  <div>
    <h2>Ledger (hash chain)</h2>
    <div class="controls" style="margin-bottom: 8px">
      <button @click="verify">Verify chain</button>
      <span class="muted">{{ verdict ?? 'every entry carries the hash of the one before it' }}</span>
    </div>
    <div class="list">
      <div v-for="e in [...ledger].reverse()" :key="e.seq" class="line">
        <span class="src">#{{ e.seq }}</span>
        <span :style="{ color: e.kind.includes('refused') || e.kind.includes('fault') ? 'var(--bad)' : e.kind.includes('ok') || e.kind.includes('open') ? 'var(--accent)' : '' }">{{ e.kind }}</span>
        {{ e.detail }}
        <span class="muted"> {{ e.prev }} → {{ e.hash }}</span>
      </div>
    </div>
  </div>
</template>
