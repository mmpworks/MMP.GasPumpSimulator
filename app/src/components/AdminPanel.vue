<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { api, state, dollars, nowMs, mmss } from '../sim'

const pin = ref('')
const credit = ref(75)
const debit = ref(50)
// Seed the inputs from the site's live values once, so the panel starts where the host is.
let seeded = false
watch(() => state.value?.site, s => {
  if (!s || seeded) return
  seeded = true
  credit.value = s.creditHoldCents / 100
  debit.value = s.debitHoldCents / 100
})
const result = ref<string | null>(null)
const site = computed(() => state.value?.site)
const lockLeft = computed(() => {
  const u = site.value?.lockedUntil
  if (!u) return null
  return Math.max(0, Math.round((Date.parse(u) - nowMs.value) / 1000))
})

async function apply(tender: 'Credit' | 'Debit', dollarsWanted: number) {
  const r = await api.adminSetHold(pin.value, tender, Math.round(dollarsWanted * 100))
  result.value = (r.ok ? 'applied: ' : 'refused: ') + r.reason
}
</script>

<template>
  <div>
    <h2>Administrator (back office)</h2>
    <div class="status">
      <span class="badge info">credit hold ceiling {{ site ? dollars(site.creditHoldCents) : '--' }}</span>
      <span class="badge info">debit hold ceiling {{ site ? dollars(site.debitHoldCents) : '--' }}</span>
      <span v-if="lockLeft !== null" class="badge bad">PANEL LOCKED {{ mmss(lockLeft) }}</span>
      <span v-else-if="site?.failedPins" class="badge warn">{{ site.failedPins }} of {{ site.maxPinFailures }} wrong PINs</span>
    </div>
    <div class="controls" style="margin-bottom: 8px">
      <label class="muted">admin PIN</label>
      <input v-model="pin" type="password" placeholder="1234" style="width: 90px" autocomplete="off" />
    </div>
    <div class="controls" style="margin-bottom: 8px">
      <label class="muted">credit $</label>
      <input type="number" v-model.number="credit" min="1" step="5" style="width: 90px" />
      <button :disabled="lockLeft !== null" @click="apply('Credit', credit)">Set credit ceiling</button>
      <label class="muted" style="margin-left: 10px">debit $</label>
      <input type="number" v-model.number="debit" min="1" step="5" style="width: 90px" />
      <button :disabled="lockLeft !== null" @click="apply('Debit', debit)">Set debit ceiling</button>
    </div>
    <div class="controls" style="margin-bottom: 8px">
      <button class="danger" @click="api.attack('pinbrute')">Attack: brute-force the PIN</button>
      <button class="danger" :disabled="lockLeft !== null" @click="apply('Debit', 5000)">Attack: debit ceiling $5000 with this PIN</button>
    </div>
    <div class="muted" v-if="result">{{ result }}</div>
    <p class="muted" style="margin: 10px 0 0">
      The site asks the bank to hold at most these amounts. Debit holds are real money out of a checking account until settlement, so stations keep
      them low. A debit sale's ceiling is the lesser of this ceiling and what the bank approves. Cash has no ceiling here: the cash amount is the ceiling.
      Raise the debit ceiling to $5000 and run a debit sale: the bank approves it, the pump clamps it to its own $500 hard limit, and the ledger says so.
      Three wrong PINs lock this panel for 5 minutes, and every attempt is in the ledger.
    </p>
  </div>
</template>
