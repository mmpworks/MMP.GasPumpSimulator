<script setup lang="ts">
import { computed } from 'vue'
import { state, flowTrace, dollars, nowMs, secondsLeft, mmss } from '../sim'

const sale = computed(() => state.value?.sale)
const pct = computed(() => sale.value && sale.value.ceilingCents > 0 ? Math.min(100, (sale.value.cents / sale.value.ceilingCents) * 100) : 0)
const maxTrace = computed(() => Math.max(1, ...flowTrace))
const left = computed(() => secondsLeft(sale.value, nowMs.value))

const noticeType = computed<'success' | 'warning' | 'error' | 'info'>(() => {
  const a = sale.value?.notice.action
  if (a === 'UseAnotherPump' || a === 'SeeAttendant') return 'error'
  if (a === 'TryAgain' || a === 'UseAnotherCard') return 'warning'
  if (a === 'Leave' || a === 'HangToStop') return 'success'
  return 'info'
})
const actionText: Record<string, string> = {
  None: '', Wait: 'Please wait', LiftNozzle: 'Lift the nozzle', HangToStop: 'Hang up to finish', Leave: 'You may go',
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
const phaseColor = computed(() => {
  const p = sale.value?.phase
  if (p === 'full') return 'success'
  if (p === 'taper' || p === 'creep' || p === 'paused') return 'warning'
  if (p === 'faulted' || p === 'declined') return 'error'
  return 'info'
})
const holdColor = computed(() => {
  const h = sale.value?.hold
  if (h === 'ReleasePending' || h === 'SettlePending') return 'warning'
  if (h === 'Released' || h === 'Settled') return 'success'
  if (h === 'Held' || h === 'Requested') return 'info'
  return undefined
})
const holdText: Record<string, string> = {
  None: 'no hold', Requested: 'hold requested', Held: 'hold on card', Settled: 'charged', Released: 'hold released',
  SettlePending: 'charge queued', ReleasePending: 'release queued',
}
</script>

<template>
  <v-card class="pane-card">
    <v-card-title>Dispenser</v-card-title>
    <v-card-text>
      <v-alert v-if="sale" :type="noticeType" :title="sale.notice.headline" class="mb-3">
        <div class="text-body-2">{{ sale.notice.detail }}</div>
        <div class="d-flex align-center ga-3 mt-2">
          <v-chip v-if="actionText[sale.notice.action]" :color="noticeType" variant="flat" size="small">{{ actionText[sale.notice.action] }}</v-chip>
          <span v-if="left !== null" class="muted" style="font-family: ui-monospace, Consolas, monospace">{{ countdownLabel }} {{ mmss(left) }}</span>
        </div>
      </v-alert>

      <div v-if="sale && state" class="face mb-3">
        <div class="row"><span class="label">THIS SALE</span><span class="big">{{ dollars(sale.cents) }}</span></div>
        <div class="row"><span class="label">GALLONS</span><span class="big">{{ sale.gallons.toFixed(3) }}</span></div>
        <div class="row"><span class="label">PRICE / GAL ({{ sale.grade }})</span><span class="mid">{{ state.prices[sale.grade] }}</span></div>
        <div class="row"><span class="label">FLOW</span><span class="mid">{{ sale.gpm.toFixed(2) }} gpm</span></div>
        <div class="row"><span class="label">CEILING</span><span class="mid">{{ sale.ceilingCents ? dollars(sale.ceilingCents) : '--' }}</span></div>
      </div>

      <div class="row-wrap mb-2">
        <v-chip color="info">{{ sale?.state }}</v-chip>
        <v-chip :color="phaseColor">{{ sale?.phase }}</v-chip>
        <v-chip :color="holdColor">{{ sale?.tender?.toLowerCase() }}: {{ holdText[sale?.hold ?? 'None'] }}</v-chip>
        <v-chip :color="state?.pump.phase === 'Faulted' ? 'error' : undefined">pump {{ state?.pump.phase }}</v-chip>
        <v-chip v-if="state?.pump.armed?.clamped" color="error" variant="flat">CLAMPED: {{ state?.pump.armed?.clampNote }}</v-chip>
      </div>

      <v-progress-linear :model-value="pct" color="success" height="10" rounded class="mb-1" />
      <div class="muted mb-2">{{ pct.toFixed(0) }}% of ceiling. Taper starts at 80%. Creep floor 8%.</div>
      <div class="trace mb-3" title="flow rate, last 240 ticks">
        <i v-for="(g, i) in flowTrace" :key="i" :style="{ height: (g / maxTrace) * 100 + '%' }"></i>
      </div>

      <v-table density="compact">
        <tbody>
          <tr><td class="muted">sale</td><td>{{ sale?.saleId }} {{ sale?.cardName ?? '' }}</td></tr>
          <tr><td class="muted">last</td><td>{{ sale?.lastMessage ?? '' }}</td></tr>
          <tr><td class="muted">fault</td><td :class="{ 'text-error': state?.pump.fault }">{{ state?.pump.fault ?? 'none' }}</td></tr>
          <tr>
            <td class="muted">bank owes</td>
            <td :class="{ 'text-warning': state?.pending.length }">
              <template v-if="state?.pending.length">{{ state.pending.map(p => `${p.kind} ${p.saleId}${p.cents ? ' ' + dollars(p.cents) : ''}`).join(', ') }} (queued)</template>
              <template v-else>nothing queued</template>
            </td>
          </tr>
          <tr><td class="muted">open holds</td><td :class="{ 'text-info': state?.openHolds.length }">{{ state?.openHolds.length ? state.openHolds.join(', ') : 'none at the bank' }}</td></tr>
          <tr><td class="muted">totalizer</td><td>{{ state?.pump.totalizerGallons.toFixed(3) }} gal (sealed, never resets)</td></tr>
        </tbody>
      </v-table>
    </v-card-text>
  </v-card>
</template>
