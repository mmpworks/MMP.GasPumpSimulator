<script setup lang="ts">
import { computed } from 'vue'
import { state, flowTrace, dollars } from '../sim'

const sale = computed(() => state.value?.sale)
const pct = computed(() => sale.value && sale.value.ceilingCents > 0 ? Math.min(100, (sale.value.cents / sale.value.ceilingCents) * 100) : 0)
const phaseClass = computed(() => {
  const p = sale.value?.phase
  if (p === 'full') return 'ok'
  if (p === 'taper' || p === 'creep') return 'warn'
  if (p === 'faulted' || p === 'declined') return 'bad'
  return 'info'
})
const maxTrace = computed(() => Math.max(1, ...flowTrace))
</script>

<template>
  <div>
    <h2>Dispenser</h2>
    <div v-if="sale && state" class="face">
      <div class="row"><span class="label">THIS SALE</span><span class="big">{{ dollars(sale.cents) }}</span></div>
      <div class="row"><span class="label">GALLONS</span><span class="big">{{ sale.gallons.toFixed(3) }}</span></div>
      <div class="row"><span class="label">PRICE / GAL ({{ sale.grade }})</span><span class="mid">{{ state.prices[sale.grade] }}</span></div>
      <div class="row"><span class="label">FLOW</span><span class="mid">{{ sale.gpm.toFixed(2) }} gpm</span></div>
      <div class="row"><span class="label">CEILING</span><span class="mid">{{ sale.ceilingCents ? dollars(sale.ceilingCents) : '--' }}</span></div>
    </div>
    <div class="status" style="margin-top: 10px">
      <span class="badge info">{{ sale?.state }}</span>
      <span class="badge" :class="phaseClass">{{ sale?.phase }}</span>
      <span class="badge" :class="state?.pump.phase === 'Faulted' ? 'bad' : ''">pump {{ state?.pump.phase }}</span>
      <span v-if="state?.pump.armed?.clamped" class="badge bad">CLAMPED: {{ state?.pump.armed?.clampNote }}</span>
    </div>
    <div class="bar"><div :style="{ width: pct + '%' }"></div></div>
    <div class="muted" style="margin-top: 4px">{{ pct.toFixed(0) }}% of ceiling. Taper starts at 80%. Creep floor 8%.</div>
    <div class="trace" title="flow rate, last 240 ticks">
      <i v-for="(g, i) in flowTrace" :key="i" :style="{ height: (g / maxTrace) * 100 + '%' }"></i>
    </div>
    <div class="kv" style="margin-top: 10px">
      <b>sale</b><span>{{ sale?.saleId }} {{ sale?.cardName ?? '' }}</span>
      <b>last</b><span>{{ sale?.lastMessage ?? '' }}</span>
      <b>fault</b><span :style="{ color: state?.pump.fault ? 'var(--bad)' : '' }">{{ state?.pump.fault ?? 'none' }}</span>
      <b>totalizer</b><span>{{ state?.pump.totalizerGallons.toFixed(3) }} gal (sealed, never resets)</span>
    </div>
  </div>
</template>
