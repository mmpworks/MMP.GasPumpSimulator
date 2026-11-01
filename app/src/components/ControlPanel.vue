<script setup lang="ts">
import { ref, computed } from 'vue'
import { api, cards, state } from '../sim'

const card = ref('Good')
const sale = computed(() => state.value?.sale)
const pump = computed(() => state.value?.pump)
const s = computed(() => sale.value?.state)
const armed = computed(() => s.value === 'ReadyToPump' || s.value === 'Fueling')
const canPresent = computed(() => !s.value || ['Idle', 'Declined', 'Cancelled', 'Faulted', 'StoreAndForward'].includes(s.value))
const canGrade = computed(() => s.value === 'ReadyToPump')
const nozzleOut = computed(() => !!pump.value?.nozzleLifted)
const seated = computed(() => !!pump.value?.nozzleSeated)
const flowing = computed(() => pump.value?.phase === 'Dispensing')
const canSqueeze = computed(() => armed.value && !flowing.value && nozzleOut.value && seated.value && !!pump.value?.selectedGrade)
const canCancel = computed(() => s.value === 'Authorizing' || s.value === 'ReadyToPump')
const grades = ['Regular', 'Plus', 'Premium']
</script>

<template>
  <div>
    <h2>Driver</h2>
    <div class="controls" style="margin-bottom: 10px">
      <select v-model="card" title="Each card is named after what it will do">
        <option v-for="c in cards" :key="c.name" :value="c.name">{{ c.name }} · {{ c.kind }} ····{{ c.last4 }} exp {{ c.expiry }}</option>
      </select>
      <button class="primary" :disabled="!canPresent" @click="api.presentCard(card)">Present card</button>
      <button :disabled="!canCancel" @click="api.cancel()">Cancel</button>
      <button @click="api.reset()">New sale</button>
    </div>

    <div class="muted" style="margin-bottom: 4px">1. Grade <span v-if="pump?.selectedGrade" class="badge ok">{{ pump.selectedGrade }}</span></div>
    <div class="controls" style="margin-bottom: 10px">
      <button v-for="g in grades" :key="g" :disabled="!canGrade" :class="{ primary: pump?.selectedGrade === g }" @click="api.selectGrade(g)">
        {{ g }} <span class="muted">{{ state?.prices[g] }}</span>
      </button>
    </div>

    <div class="muted" style="margin-bottom: 4px">2. Nozzle
      <span class="badge" :class="nozzleOut ? 'info' : ''">{{ nozzleOut ? 'off harness' : 'on harness' }}</span>
      <span class="badge" :class="seated ? 'ok' : ''">{{ seated ? 'in tank' : 'not in tank' }}</span>
    </div>
    <div class="grid2" style="margin-bottom: 10px">
      <button v-if="!nozzleOut" :disabled="!armed" @click="api.lift()">Lift nozzle</button>
      <button v-else :disabled="!armed" @click="api.hang()">Hang nozzle{{ pump?.poured ? ' (ends sale)' : '' }}</button>
      <button v-if="!seated" :disabled="!armed || !nozzleOut" @click="api.seat()">Put in tank</button>
      <button v-else :disabled="!armed" @click="api.unseat()">Pull out of tank</button>
    </div>

    <div class="muted" style="margin-bottom: 4px">3. Trigger <span class="badge" :class="flowing ? 'ok' : ''">{{ flowing ? 'flowing' : 'closed' }}</span></div>
    <div class="grid2" style="margin-bottom: 10px">
      <button class="primary" :disabled="!canSqueeze" @click="api.squeeze()">Squeeze trigger</button>
      <button :disabled="!flowing" @click="api.release()">Release trigger</button>
    </div>

    <button :disabled="!state?.pending.length" @click="api.drain()">Drain queued to bank ({{ state?.pending.length ?? 0 }})</button>
    <p class="muted" style="margin: 10px 0 0">
      Fuel flows only with a grade chosen, the nozzle in the tank, and the trigger held. Release and squeeze as often as you like.
      Once anything has poured, hanging the nozzle ends the sale. Walk away after approval and the pump cancels itself at 120 s.
    </p>
  </div>
</template>
