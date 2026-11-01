<script setup lang="ts">
import { computed } from 'vue'
import { state, flowTrace, dollars, nowMs, secondsLeft, mmss } from '../sim'

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

// The driver's screen. One headline, one line of detail, one next step, and a countdown when a timer is running.
const left = computed(() => secondsLeft(sale.value, nowMs.value))
const noticeClass = computed(() => {
  const a = sale.value?.notice.action
  if (a === 'UseAnotherPump' || a === 'SeeAttendant') return 'bad'
  if (a === 'TryAgain' || a === 'UseAnotherCard') return 'warn'
  if (a === 'Leave' || a === 'HangToStop') return 'ok'
  return 'info'
})
const actionText: Record<string, string> = {
  None: '', Wait: 'Please wait', LiftNozzle: 'Lift the nozzle', HangToStop: 'Hang up to stop', Leave: 'You may go',
  TryAgain: 'Try again or use another pump', UseAnotherCard: 'Use another card', UseAnotherPump: 'Use another pump', SeeAttendant: 'See the attendant',
}
const countdownLabel = computed(() => {
  const s = sale.value?.state
  if (s === 'Authorizing') return 'gives up in'
  if (s === 'ReadyToPump') return 'cancels in'
  if (s === 'Fueling') return 'time limit in'
  if (s === 'Completing') return 'queues in'
  return 'screen clears in'
})
const holdClass = computed(() => {
  const h = sale.value?.hold
  if (h === 'ReleasePending' || h === 'SettlePending') return 'warn'
  if (h === 'Released' || h === 'Settled') return 'ok'
  if (h === 'Held' || h === 'Requested') return 'info'
  return ''
})
const holdText: Record<string, string> = {
  None: 'no hold', Requested: 'hold requested', Held: 'hold on card', Settled: 'charged', Released: 'hold released',
  SettlePending: 'charge queued, bank link down', ReleasePending: 'release queued, bank link down',
}
</script>

<template>
  <div>
    <h2>Dispenser</h2>
    <div v-if="sale" class="notice" :class="noticeClass">
      <div class="headline">{{ sale.notice.headline }}</div>
      <div class="detail">{{ sale.notice.detail }}</div>
      <div class="foot">
        <span class="badge" :class="noticeClass">{{ actionText[sale.notice.action] }}</span>
        <span v-if="left !== null" class="countdown">{{ countdownLabel }} {{ mmss(left) }}</span>
      </div>
    </div>
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
      <span class="badge" :class="holdClass" title="where the driver's money is">{{ holdText[sale?.hold ?? 'None'] }}</span>
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
      <b>bank owes</b><span :style="{ color: state?.pending.length ? 'var(--warn)' : '' }">
        <template v-if="state?.pending.length">{{ state.pending.map(p => `${p.kind} ${p.saleId}${p.cents ? ' ' + dollars(p.cents) : ''}`).join(', ') }} (queued, drains when the link returns)</template>
        <template v-else>nothing queued</template>
      </span>
      <b>open holds</b><span :style="{ color: state?.openHolds.length ? 'var(--info)' : '' }">{{ state?.openHolds.length ? state.openHolds.join(', ') : 'none at the bank' }}</span>
      <b>totalizer</b><span>{{ state?.pump.totalizerGallons.toFixed(3) }} gal (sealed, never resets)</span>
    </div>
  </div>
</template>
