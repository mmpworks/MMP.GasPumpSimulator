<script setup lang="ts">
import { ref, watch } from 'vue'
import { api, state } from '../sim'

const latency = ref(250)
const flap = ref(50)
watch(() => state.value?.network.latencyMs, v => { if (v !== undefined) latency.value = v })
const modes = ['Up', 'Down', 'Timeout', 'Flapping']
</script>

<template>
  <div>
    <h2>Host link</h2>
    <div class="controls" style="margin-bottom: 10px">
      <button v-for="m in modes" :key="m" :class="{ primary: state?.network.mode === m }" @click="api.network(m)">{{ m }}</button>
    </div>
    <div class="controls">
      <label class="muted">latency</label>
      <input type="number" v-model.number="latency" min="0" max="60000" step="50" style="width: 90px" @change="api.network(undefined, latency)" />
      <span class="muted">ms</span>
      <label class="muted" style="margin-left: 10px">flap fail</label>
      <input type="number" v-model.number="flap" min="0" max="100" step="10" style="width: 70px" @change="api.network(undefined, undefined, flap / 100)" />
      <span class="muted">%</span>
    </div>
    <p class="muted" style="margin: 10px 0 0">
      Down refuses at once. Timeout swallows the request and the 30 s host timeout fires. Latency at or above 30000 ms is a timeout too.
      Pull the link after fueling to see a completion queue, then put it back and drain.
    </p>
  </div>
</template>
