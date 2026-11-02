<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { state, flowTrace, dollars, nowMs, secondsLeft, mmss } from '../sim'

const sale = computed(() => state.value?.sale)
const recovery = computed(() => state.value?.recovery && state.value.recovery.kind !== 'CleanStart' ? state.value.recovery : null)
const showRecovery = ref(true)
// A new run means a new recovery record; show it again.
watch(() => state.value?.runId, () => { showRecovery.value = true })
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
  None: '', Wait: 'Please wait', EnterPin: 'Enter your PIN', EnterZip: 'Enter your ZIP code', LiftNozzle: 'Lift the nozzle', HangToStop: 'Hang up to finish', Leave: 'You may go',
  TryAgain: 'Try again or use another pump', UseAnotherCard: 'Use another card', UseAnotherPump: 'Use another pump', SeeAttendant: 'See the attendant',
}
const countdownLabel = computed(() => {
  const s = sale.value?.state
  if (s === 'CardRead') return 'cancels in'
  if (s === 'Authorizing') return 'gives up in'
  if (s === 'ReadyToPump') return 'cancels in'
  if (s === 'Fueling' && sale.value?.phase === 'paused') return 'finishes in'
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
    <v-card-title>Pump face</v-card-title>
    <v-card-text>
      <!-- What the station's restart did about the sale it found open. Stays until the operator closes it. -->
      <v-alert v-if="recovery && showRecovery" :type="recovery.underRecorded ? 'error' : 'warning'" density="compact" class="mb-3" closable
               :title="recovery.kind === 'ClosedOut' ? 'Station restarted mid-sale: closed out from the journal' : recovery.kind === 'Reversed' ? 'Station restarted: open sale reversed' : 'Station restarted: completion sent again'"
               @click:close="showRecovery = false">
        <div class="text-body-2">{{ recovery.note }}</div>
      </v-alert>
      <!-- The service strip: which parts hold the pump out of service, and what clears each. Lit only while something does. -->
      <div v-if="state && !state.pump.inService" class="service-strip mb-2">
        <v-icon size="18" color="error">mdi-alert-octagon-outline</v-icon>
        <b>OUT OF SERVICE</b>
        <v-chip v-for="(c, i) in state.pump.serviceCodes" :key="c" size="small" color="error" variant="flat" :title="state.pump.serviceParts[i]">{{ c }}</v-chip>
        <span class="muted">{{ state.pump.serviceParts.join(' · ') }}</span>
      </div>
      <v-alert v-if="sale" :type="noticeType" :title="sale.notice.headline" class="mb-3 notice">
        <div class="text-body-2">{{ sale.notice.detail }}</div>
        <div class="d-flex align-center ga-3 mt-2">
          <v-chip v-if="actionText[sale.notice.action]" :color="noticeType" variant="flat" size="small">{{ actionText[sale.notice.action] }}</v-chip>
          <span v-if="left !== null" class="muted" style="font-family: ui-monospace, Consolas, monospace">{{ countdownLabel }} {{ mmss(left) }}</span>
        </div>
      </v-alert>

      <div v-if="sale && state" class="face mb-3">
        <div class="row"><span class="label">THIS SALE</span><span class="big">{{ dollars(sale.cents) }}</span></div>
        <div class="row"><span class="label">GALLONS</span><span class="big">{{ sale.gallons.toFixed(3) }}</span></div>
        <div class="row"><span class="label">PRICE / GAL ({{ sale.grade }})</span><span class="mid" data-testid="face-price">{{ state.prices[sale.grade] }}</span></div>
        <div class="row"><span class="label">FLOW</span><span class="mid">{{ sale.gpm.toFixed(2) }} gpm</span></div>
        <div class="row"><span class="label">CEILING</span><span class="mid">{{ sale.ceilingCents ? dollars(sale.ceilingCents) : '--' }}</span></div>
      </div>

      <div class="row-wrap mb-2">
        <v-chip color="info">{{ sale?.state }}</v-chip>
        <v-chip :color="phaseColor">{{ sale?.phase }}</v-chip>
        <v-chip :color="holdColor">{{ sale?.tender?.toLowerCase() }}: {{ holdText[sale?.hold ?? 'None'] }}</v-chip>
        <v-chip v-if="sale && sale.state !== 'Idle'" variant="outlined">{{ sale.source === 'Station' ? 'set at the station' : sale.source === 'PumpTap' ? 'tapped at the pump' : 'inserted at the pump' }}</v-chip>
        <v-chip :color="state?.pump.phase === 'Faulted' ? 'error' : undefined">pump {{ state?.pump.phase }}</v-chip>
        <v-chip v-if="sale?.faultCode" color="error" variant="flat" :title="sale.faultPart ?? ''">{{ sale.faultCode }}</v-chip>
        <v-chip v-if="state?.pump.breakawayOpen" color="error">breakaway open</v-chip>
        <v-chip v-if="state && !state.pump.supplyAvailable" color="warning">no supply</v-chip>
        <v-chip v-if="state?.pump.pulserDead" color="warning">pulser dead</v-chip>
        <v-chip v-if="state?.pump.armed?.clamped" color="error" variant="flat">CLAMPED: {{ state?.pump.armed?.clampNote }}</v-chip>
        <!-- The pump needs the bank only at authorization and settlement. During a sale a dead link changes nothing on the hose. -->
        <v-chip v-if="state && state.network.mode !== 'Up'" color="warning" prepend-icon="mdi-lan-disconnect"
                :title="sale?.state === 'Fueling' ? 'fuel keeps flowing on the token; settlement will queue' : 'authorizations will fail until the link is back'">
          bank link {{ state.network.mode.toLowerCase() }}
        </v-chip>
      </div>

      <v-progress-linear :model-value="pct" color="success" height="10" rounded class="mb-1" />
      <div class="muted mb-2">{{ pct.toFixed(0) }}% of ceiling. Taper starts at 80%. Creep floor 8%.</div>
      <div class="trace mb-3" title="flow rate, last 240 ticks">
        <i v-for="(g, i) in flowTrace" :key="i" :style="{ height: (g / maxTrace) * 100 + '%' }"></i>
      </div>

      <div class="kv">
        <b>sale</b><span>{{ sale?.saleId }} {{ sale?.cardName ?? '' }}</span>
        <b>last</b><span>{{ sale?.lastMessage ?? '' }}</span>
        <b>fault</b><span :class="{ 'text-error': state?.pump.fault }">{{ state?.pump.fault ?? 'none' }}</span>
        <b>totalizer</b><span>{{ state?.pump.totalizerGallons.toFixed(3) }} gal, sealed, owned by the dispenser</span>
        <b>armed</b><span>{{ state?.pump.armed ? `${state.pump.armed.saleId} ceiling ${dollars(state.pump.armed.ceilingCents)}, max ${state.pump.armed.maxGallons.toFixed(1)} gal` : 'no authorization held' }}</span>
      </div>
    </v-card-text>
  </v-card>
</template>
