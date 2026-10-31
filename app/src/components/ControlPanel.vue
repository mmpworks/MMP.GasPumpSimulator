<script setup lang="ts">
import { ref, computed } from 'vue'
import { api, cards, state } from '../sim'

const card = ref('Good')
const grade = ref('Regular')
const s = computed(() => state.value?.sale.state)
const canPresent = computed(() => !s.value || ['Idle', 'Declined', 'Cancelled', 'Faulted', 'StoreAndForward'].includes(s.value))
const canLift = computed(() => s.value === 'ReadyToPump')
const canHang = computed(() => s.value === 'Fueling')
const canCancel = computed(() => s.value === 'Authorizing' || s.value === 'ReadyToPump')
</script>

<template>
  <div>
    <h2>Driver</h2>
    <div class="controls" style="margin-bottom: 10px">
      <select v-model="card" title="Each card is named after what it will do">
        <option v-for="c in cards" :key="c.name" :value="c.name">{{ c.name }} · {{ c.kind }} ····{{ c.last4 }} exp {{ c.expiry }}</option>
      </select>
      <select v-model="grade">
        <option>Regular</option><option>Plus</option><option>Premium</option>
      </select>
    </div>
    <div class="grid2">
      <button class="primary" :disabled="!canPresent" @click="api.presentCard(card, grade)">Present card</button>
      <button :disabled="!canLift" @click="api.lift()">Lift nozzle</button>
      <button :disabled="!canHang" @click="api.hang()">Hang nozzle</button>
      <button :disabled="!canCancel" @click="api.cancel()">Cancel</button>
      <button @click="api.reset()">New sale</button>
      <button :disabled="!state?.pendingCompletions.length" @click="api.drain()">Drain queued ({{ state?.pendingCompletions.length ?? 0 }})</button>
    </div>
    <p class="muted" style="margin: 10px 0 0">
      Hold is $30. Good approves $30. LowBalance approves $18.50 (partial). The rest decline for the reason in their name.
      Lift within 120 s or the hold is reversed.
    </p>
  </div>
</template>
