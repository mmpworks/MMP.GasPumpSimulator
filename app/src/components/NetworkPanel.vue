<script setup lang="ts">
import { ref, watch } from 'vue'
import { api, state } from '../sim'

const latency = ref(250)
const late = ref(45)
const flap = ref(50)
watch(() => state.value?.network.latencyMs, v => { if (v !== undefined) latency.value = v })
watch(() => state.value?.network.lateResponseMs, v => { if (v !== undefined) late.value = Math.round(v / 1000) })
const modes = [
  { value: 'Up', hint: 'answers after the latency' },
  { value: 'Down', hint: 'refuses at once' },
  { value: 'Timeout', hint: 'the request vanishes; the 30 s host timeout fires' },
  { value: 'Flapping', hint: 'a coin flip per call between Up and Timeout' },
  { value: 'Late', hint: 'the bank hears us and acts, but the answer comes back after the pump gave up' },
]
function setMode(m: string | null) { if (m) api.network(m) }
</script>

<template>
  <div>
    <div class="step">Mode</div>
    <div class="row-wrap mb-3">
      <v-btn v-for="m in modes" :key="m.value" :title="m.hint" :color="state?.network.mode === m.value ? 'primary' : undefined"
             :variant="state?.network.mode === m.value ? 'flat' : 'outlined'" @click="setMode(m.value)">{{ m.value }}</v-btn>
    </div>
    <div class="row-wrap mb-3">
      <v-text-field v-model.number="latency" type="number" label="latency, ms" min="0" max="60000" step="50" style="max-width: 150px" @change="api.network(undefined, latency)" />
      <v-text-field v-model.number="late" type="number" label="late answer, s" min="1" max="600" step="5" style="max-width: 150px" @change="api.network(undefined, undefined, undefined, late * 1000)" />
      <v-text-field v-model.number="flap" type="number" label="flap fail, %" min="0" max="100" step="10" style="max-width: 150px" @change="api.network(undefined, undefined, flap / 100)" />
    </div>
    <div class="step">Station</div>
    <v-alert v-if="state?.station.estopActive" type="error" variant="flat" class="mb-2" title="E-STOP ACTIVE">
      Power is off at every dispenser and every submersible pump. Reset at the cashier to restore it.
      <div class="mt-2"><v-btn variant="outlined" prepend-icon="mdi-restart" @click="api.estopReset()">Reset at the cashier</v-btn></div>
    </v-alert>
    <div v-else class="row-wrap mb-3">
      <v-btn color="error" variant="flat" size="large" prepend-icon="mdi-alert-octagon" @click="api.estop('forecourt button')">E-STOP</v-btn>
      <v-btn color="error" prepend-icon="mdi-power-plug-off" @click="api.powerLoss()">Pull the plug (one pump)</v-btn>
      <span class="muted">E-stop writes the record first, then drops power station-wide and waits for the cashier. The plug is a bare crash: no record, no final save.</span>
    </div>

    <div class="step">Forecourt faults</div>
    <div class="row-wrap mb-3">
      <v-btn v-if="state?.pump.supplyAvailable !== false" color="warning" prepend-icon="mdi-gas-station-off" :disabled="state?.station.estopActive" @click="api.fault('tankout')">Tank runs dry</v-btn>
      <v-btn v-else color="success" prepend-icon="mdi-gas-station" @click="api.fault('tankback')">Tank refilled</v-btn>
      <v-btn v-if="!state?.pump.pulserDead" color="warning" prepend-icon="mdi-counter" :disabled="state?.station.estopActive" @click="api.fault('pulserdead')">Kill the pulser</v-btn>
      <v-btn v-else color="success" prepend-icon="mdi-counter" @click="api.fault('pulserfix')">Fix the pulser</v-btn>
      <v-btn v-if="!state?.pump.breakawayOpen" color="warning" prepend-icon="mdi-car-off" :disabled="state?.station.estopActive" @click="api.fault('driveoff')">Drive-off (breakaway)</v-btn>
      <v-btn v-else color="success" prepend-icon="mdi-wrench" @click="api.fault('breakawayreset')">Attendant resets the breakaway</v-btn>
    </div>
    <p class="muted mb-3">
      Each fault carries a diagnostic code into the log, the ledger, and the custody file: <code>PUMP.STP.NOFLOW</code> for a dry tank,
      <code>PUMP.MTR.NOPULSE</code> for a dead pulser, <code>PUMP.HOSE.BREAKAWAY</code> for a drive-off, <code>STATION.ESTOP</code> for the button.
    </p>
    <p class="muted mb-0">
      <b>Late</b> is the saga case. Present a card: the bank puts the hold on at 250 ms, the pump gives up at 30 s and sends a reversal,
      the approval arrives at 45 s onto a sale that already ended, and the pump reverses again. Watch the hold chip go
      hold requested → release queued → hold released. Pull the link <b>Down</b> after fueling to see a completion queue, then put it back and drain.
    </p>
  </div>
</template>
