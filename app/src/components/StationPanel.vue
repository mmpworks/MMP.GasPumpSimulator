<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { api, state, dollars, pumpState } from '../sim'
import NetworkPanel from './NetworkPanel.vue'
import AdminPanel from './AdminPanel.vue'

// Everything the station does that is not a sale: the bank link, the back office, the E-stop, the journal.
type Tab = 'bank' | 'admin' | 'station'
const tab = ref<Tab>('bank')
try { const saved = localStorage.getItem('gaspump.station-tab') as Tab | null; if (saved) tab.value = saved } catch { /* private window */ }
watch(tab, t => { try { localStorage.setItem('gaspump.station-tab', t) } catch { /* ignore */ } })

const bankHint = computed(() => state.value?.network.mode ?? '')
const adminLocked = computed(() => !!state.value?.site.lockedUntil)
const estop = computed(() => !!state.value?.station.estopActive)
const recovery = computed(() => pumpState.value?.recovery && pumpState.value!.recovery.kind !== 'CleanStart' ? pumpState.value!.recovery : null)
const d = computed(() => pumpState.value?.dispenser)
const pumpClosed = computed(() => pumpState.value?.pump.phase === 'Closed' || !!pumpState.value?.pump.serviceParts.includes('pump switch'))
const pumpCalling = computed(() => pumpState.value?.pump.phase === 'Calling')
const totals = computed(() => pumpState.value?.totals ?? null)
</script>

<template>
  <v-card class="pane-card fixed">
    <v-tabs v-model="tab" density="compact" color="primary" grow>
      <v-tab value="bank" prepend-icon="mdi-bank" class="text-none">Bank link <v-chip class="ml-2" size="x-small">{{ bankHint }}</v-chip></v-tab>
      <v-tab value="admin" prepend-icon="mdi-shield-account" class="text-none">Administrator <v-chip v-if="adminLocked" class="ml-2" size="x-small" color="error" variant="flat">LOCKED</v-chip></v-tab>
      <v-tab value="station" prepend-icon="mdi-alert-octagon" class="text-none">Power <v-chip v-if="estop" class="ml-2" size="x-small" color="error" variant="flat">E-STOP</v-chip></v-tab>
    </v-tabs>
    <v-divider />
    <v-card-text>
      <NetworkPanel v-show="tab === 'bank'" />
      <AdminPanel v-show="tab === 'admin'" />
      <div v-show="tab === 'station'">
        <div class="step">Pump switch <v-chip v-if="pumpClosed" size="small" color="warning" variant="flat">CLOSED</v-chip><v-chip v-else-if="pumpCalling" size="small" color="info" variant="flat">CALLING</v-chip></div>
        <div class="row-wrap mb-3">
          <v-btn v-if="!pumpClosed" color="warning" variant="flat" prepend-icon="mdi-lock" :disabled="estop" @click="api.closePump('operator')">Close pump for sales</v-btn>
          <v-btn v-else color="success" variant="flat" prepend-icon="mdi-lock-open-variant" :disabled="estop" @click="api.openPump('operator')">Open pump for sales</v-btn>
          <span class="muted">Closed is a state, never a fault. The pump keeps working, refuses every card at the reader and the counter, and finishes a delivery in progress first. It stays closed across a restart until opened here.</span>
        </div>

        <div class="step">Pump totals</div>
        <div class="row-wrap mb-2">
          <v-btn color="primary" variant="tonal" prepend-icon="mdi-counter" :disabled="estop" @click="api.readTotals()">Read pump totals</v-btn>
          <span class="muted">The inspector's close-out: the station asks the island for its sealed counter and its per-grade totals. The answer is a ledger line, <code>totals.read</code>, and the chips below.</span>
        </div>
        <div v-if="totals" class="row-wrap mb-3" data-testid="totals">
          <v-chip size="small" color="info" variant="flat">totalizer {{ totals.totalizerGallons.toFixed(3) }} gal</v-chip>
          <v-chip v-for="g in totals.byGrade" :key="g.grade" size="small">{{ g.grade }} {{ g.gallons.toFixed(3) }} gal · {{ dollars(g.cents) }} · {{ g.deliveries }} deliveries</v-chip>
          <v-chip v-if="totals.byGrade.length === 0" size="small" variant="text">no deliveries yet</v-chip>
          <v-chip size="small" variant="text">last tx #{{ totals.lastTxSeq }}</v-chip>
        </div>

        <div class="step">Emergency stop</div>
        <v-alert v-if="estop" type="error" variant="flat" class="mb-2" title="E-STOP ACTIVE">
          Power is off at every dispenser and every submersible pump. The record was written before the power dropped. Reset at the cashier to restore it.
          <div class="mt-2"><v-btn variant="outlined" prepend-icon="mdi-restart" @click="api.estopReset()">Reset at the cashier</v-btn></div>
        </v-alert>
        <div v-else class="row-wrap mb-3">
          <v-btn color="error" variant="flat" size="large" prepend-icon="mdi-alert-octagon" @click="api.estop('forecourt button')">E-STOP</v-btn>
          <span class="muted">Station-wide. The station writes <code>station.estop</code> and a final snapshot, then cuts the dispenser's power{{ d?.link === 'socket' ? ': GasPump.Dispenser.exe exits' : '' }}. Reset restores power to the island first, then rebuilds the station.</span>
        </div>

        <div class="step">Station power</div>
        <div class="row-wrap mb-3">
          <v-btn color="error" prepend-icon="mdi-power-plug-off" :disabled="estop" @click="api.powerLoss()">Pull the station's plug</v-btn>
          <span class="muted">A bare crash of this process: no record, no final save. {{ d?.link === 'socket' ? 'The dispenser keeps running on its own limits; the new station tells it Stop first, then closes the sale from the journal.' : 'The new process closes the open sale from the last one-second journal.' }}</span>
        </div>

        <div class="step">Last restart</div>
        <v-alert v-if="recovery" :type="recovery.underRecorded ? 'error' : 'warning'" density="compact" variant="tonal" class="mb-2"
                 :title="recovery.kind === 'ClosedOut' ? 'Closed out from the journal' : recovery.kind === 'Reversed' ? 'Open sale reversed' : 'Completion sent again'">
          <div class="text-body-2">{{ recovery.note }}</div>
          <div class="row-wrap mt-2">
            <v-chip size="small">{{ recovery.saleId }} · {{ recovery.tender }} · {{ recovery.grade }}</v-chip>
            <v-chip size="small">{{ recovery.gallons.toFixed(3) }} gal for {{ dollars(recovery.cents) }}</v-chip>
            <v-chip v-if="recovery.underRecorded" size="small" color="error" variant="flat">up to {{ recovery.unrecordedUpToGallons.toFixed(3) }} gal unrecorded</v-chip>
            <v-chip size="small" variant="text">journal was {{ recovery.snapshotAgeSeconds.toFixed(1) }} s old</v-chip>
          </div>
        </v-alert>
        <p v-else class="muted">Clean start. Journal at <code>{{ state?.custody.journalPath?.split(/[\\/]/).pop() }}</code>, saved every second and on every sale change.</p>
      </div>
    </v-card-text>
  </v-card>
</template>
