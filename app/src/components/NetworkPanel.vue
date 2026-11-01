<script setup lang="ts">
import { ref, watch } from 'vue'
import { api, state } from '../sim'

const latency = ref(250)
const late = ref(45)
const flap = ref(50)
watch(() => state.value?.network.latencyMs, v => { if (v !== undefined) latency.value = v })
watch(() => state.value?.network.lateResponseMs, v => { if (v !== undefined) late.value = Math.round(v / 1000) })
const modes = ['Up', 'Down', 'Timeout', 'Flapping', 'Late']
const hint: Record<string, string> = {
  Up: 'answers after the latency',
  Down: 'refuses at once',
  Timeout: 'the request vanishes; the 30 s host timeout fires',
  Flapping: 'a coin flip per call between Up and Timeout',
  Late: 'the bank hears us and acts, but the answer comes back after the pump gave up',
}
</script>

<template>
  <div>
    <h2>Host link</h2>
    <div class="controls" style="margin-bottom: 10px">
      <button v-for="m in modes" :key="m" :class="{ primary: state?.network.mode === m }" :title="hint[m]" @click="api.network(m)">{{ m }}</button>
    </div>
    <div class="controls">
      <label class="muted">latency</label>
      <input type="number" v-model.number="latency" min="0" max="60000" step="50" style="width: 90px" @change="api.network(undefined, latency)" />
      <span class="muted">ms</span>
      <label class="muted" style="margin-left: 10px">late answer</label>
      <input type="number" v-model.number="late" min="1" max="600" step="5" style="width: 70px" @change="api.network(undefined, undefined, undefined, late * 1000)" />
      <span class="muted">s</span>
      <label class="muted" style="margin-left: 10px">flap fail</label>
      <input type="number" v-model.number="flap" min="0" max="100" step="10" style="width: 70px" @change="api.network(undefined, undefined, flap / 100)" />
      <span class="muted">%</span>
    </div>
    <p class="muted" style="margin: 10px 0 0">
      <b>Late</b> is the saga case. Present a card: the bank puts the hold on at 250 ms, the pump gives up at 30 s and sends a reversal,
      the approval arrives at 45 s onto a sale that already ended, and the pump reverses again. Watch the <i>hold</i> badge go
      hold requested → release queued → hold released. Pull the link <b>Down</b> after fueling to see a completion queue, then put it back and drain.
    </p>
  </div>
</template>
