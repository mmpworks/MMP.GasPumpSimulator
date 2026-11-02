<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { api, state, dollars, nowMs, mmss } from '../sim'

const pin = ref('')
const credit = ref(75)
const debit = ref(50)
const result = ref<{ ok: boolean; text: string } | null>(null)
const site = computed(() => state.value?.site)
// The price fields hold dollars with three decimals ($3.499); the wire carries mills (3499).
const GRADES = ['Regular', 'Plus', 'Premium'] as const
const priceFields = ref<Record<string, number>>({ Regular: 3.499, Plus: 3.799, Premium: 4.099 })
const pumpPrices = computed(() => state.value?.prices ?? {})
const pricesInSync = computed(() => state.value?.pricesInSync ?? true)
let pricesSeeded = false
watch(() => state.value?.priceBoard, b => {
  if (!b || pricesSeeded) return
  pricesSeeded = true
  for (const g of GRADES) if (b[g] !== undefined) priceFields.value[g] = b[g] / 1000
})

async function setPrice(grade: string) {
  const mills = Math.round(priceFields.value[grade] * 1000)
  const r = await api.adminSetPrice(pin.value, grade, mills)
  result.value = { ok: r.ok, text: (r.ok ? 'Price written: ' : 'Refused: ') + r.reason }
}
const lockLeft = computed(() => {
  const u = site.value?.lockedUntil
  if (!u) return null
  return Math.max(0, Math.round((Date.parse(u) - nowMs.value) / 1000))
})
let seeded = false
watch(() => state.value?.site, s => {
  if (!s || seeded) return
  seeded = true
  credit.value = s.creditHoldCents / 100
  debit.value = s.debitHoldCents / 100
})

async function apply(tender: 'Credit' | 'Debit', dollarsWanted: number) {
  const r = await api.adminSetHold(pin.value, tender, Math.round(dollarsWanted * 100))
  result.value = { ok: r.ok, text: (r.ok ? 'Applied: ' : 'Refused: ') + r.reason }
}
</script>

<template>
  <div>
    <div class="row-wrap mb-3">
      <v-chip color="info">credit hold ceiling {{ site ? dollars(site.creditHoldCents) : '--' }}</v-chip>
      <v-chip color="info">debit hold ceiling {{ site ? dollars(site.debitHoldCents) : '--' }}</v-chip>
      <v-chip v-if="lockLeft !== null" color="error" variant="flat">PANEL LOCKED {{ mmss(lockLeft) }}</v-chip>
      <v-chip v-else-if="site?.failedPins" color="warning">{{ site.failedPins }} of {{ site.maxPinFailures }} wrong PINs</v-chip>
    </div>
    <div class="row-wrap mb-2">
      <v-text-field v-model="pin" type="password" label="admin PIN" placeholder="1234" autocomplete="off" prepend-inner-icon="mdi-lock" style="max-width: 160px" />
    </div>
    <div class="row-wrap mb-2">
      <!-- Each field travels with its button when the row wraps. -->
      <div class="d-inline-flex align-center ga-2">
        <v-text-field v-model.number="credit" type="number" label="credit ceiling, $" min="1" step="5" style="width: 150px" />
        <v-btn :disabled="lockLeft !== null" @click="apply('Credit', credit)">Set credit</v-btn>
      </div>
      <div class="d-inline-flex align-center ga-2">
        <v-text-field v-model.number="debit" type="number" label="debit ceiling, $" min="1" step="5" style="width: 150px" />
        <v-btn :disabled="lockLeft !== null" @click="apply('Debit', debit)">Set debit</v-btn>
      </div>
    </div>
    <div class="step">Posted prices <v-chip v-if="!pricesInSync" size="small" color="warning" variant="flat" data-testid="prices-pending">pump still on the old board</v-chip></div>
    <div class="row-wrap mb-2" data-testid="price-board">
      <div v-for="g in GRADES" :key="g" class="d-inline-flex align-center ga-2">
        <v-text-field v-model.number="priceFields[g]" type="number" :label="`${g}, $/gal`" min="0.001" step="0.01" style="width: 150px" />
        <v-btn :disabled="lockLeft !== null" @click="setPrice(g)">Set {{ g }}</v-btn>
        <span class="muted" :data-testid="`pump-price-${g}`">pump: {{ pumpPrices[g] ?? '--' }}</span>
      </div>
    </div>
    <div class="row-wrap mb-3">
      <v-btn color="error" prepend-icon="mdi-dots-horizontal" @click="api.attack('pinbrute')">Attack: brute-force the PIN</v-btn>
      <v-btn color="error" prepend-icon="mdi-arrow-up-bold" :disabled="lockLeft !== null" @click="apply('Debit', 5000)">Attack: debit ceiling $5000 with this PIN</v-btn>
    </div>
    <v-alert v-if="result" :type="result.ok ? 'success' : 'error'" class="mb-3">{{ result.text }}</v-alert>
    <p class="muted mb-0">
      The site asks the bank to hold at most these amounts. Debit holds are real money out of a checking account until settlement, so stations keep
      them low. A debit sale's ceiling is the lesser of this ceiling and what the bank approves. Cash has no ceiling here: the cash amount is the ceiling.
      Raise the debit ceiling to $5000 and run a debit sale: the bank approves it, the pump clamps it to its own $500 hard limit, and the ledger says so.
      Three wrong PINs lock this panel for 5 minutes, and every attempt is in the ledger.
      Prices are the station's to post: each change is written to the island as a whole board, the pump answers, and the face shows what the pump bills by.
      A change during a delivery waits for that delivery to end, so the driver pays the price they saw.
    </p>
  </div>
</template>
