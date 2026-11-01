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
    <div class="step">Power</div>
    <div class="row-wrap mb-3">
      <v-btn color="error" variant="flat" prepend-icon="mdi-power-plug-off" @click="api.powerLoss()">Pull the plug (restart the pump)</v-btn>
      <span class="muted">No close-out, no final save. The new process reads the last one-second journal and closes the open sale for what was recorded.</span>
    </div>
    <p class="muted mb-0">
      <b>Late</b> is the saga case. Present a card: the bank puts the hold on at 250 ms, the pump gives up at 30 s and sends a reversal,
      the approval arrives at 45 s onto a sale that already ended, and the pump reverses again. Watch the hold chip go
      hold requested → release queued → hold released. Pull the link <b>Down</b> after fueling to see a completion queue, then put it back and drain.
    </p>
  </div>
</template>
