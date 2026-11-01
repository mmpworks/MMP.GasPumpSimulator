<script setup lang="ts">
import { ref, computed } from 'vue'
import { api, cards, state, dollars } from '../sim'

const tender = ref<'Credit' | 'Debit' | 'Cash'>('Credit')
const card = ref('Good')
const cashDollars = ref(20)
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
const cardItems = computed(() => cards.value.map(c => ({ title: `${c.name} · ${c.kind} ····${c.last4} exp ${c.expiry}`, value: c.name })))
const changeDue = computed(() => state.value?.cash.changeDue.find(c => c.saleId === sale.value?.saleId))
const siteCeiling = computed(() => state.value && tender.value !== 'Cash' ? dollars(tender.value === 'Debit' ? state.value.site.debitHoldCents : state.value.site.creditHoldCents) : null)

function start() {
  if (tender.value === 'Cash') api.payCash(Math.round(cashDollars.value * 100))
  else api.presentCard(card.value, tender.value)
}
function selectedGrade(g: string | null) { if (g) api.selectGrade(g) }
</script>

<template>
  <v-card class="pane-card">
    <v-card-title>Driver</v-card-title>
    <v-card-text>
      <div class="step">Pay with</div>
      <div class="row-wrap mb-2">
        <v-btn v-for="t in (['Credit', 'Debit', 'Cash'] as const)" :key="t" :color="tender === t ? 'primary' : undefined"
               :variant="tender === t ? 'flat' : 'outlined'" @click="tender = t">{{ t }}</v-btn>
        <span v-if="siteCeiling" class="muted">site ceiling {{ siteCeiling }}</span>
      </div>
      <div class="row-wrap mb-2">
        <v-select v-if="tender !== 'Cash'" v-model="card" :items="cardItems" label="card" style="min-width: 260px; max-width: 360px" />
        <v-text-field v-else v-model.number="cashDollars" type="number" label="cash to cashier, $" min="1" step="5" style="max-width: 160px" />
        <v-btn color="primary" variant="flat" :disabled="!canPresent" @click="start">
          {{ tender === 'Cash' ? `Pay cashier ${dollars(Math.round(cashDollars * 100))}` : 'Present card' }}
        </v-btn>
        <v-btn :disabled="!canCancel" @click="api.cancel()">Cancel</v-btn>
        <v-btn variant="text" @click="api.reset()">New sale</v-btn>
      </div>
      <v-alert v-if="changeDue" type="warning" class="mb-2">
        <div class="d-flex align-center ga-3">
          <span>Cashier owes you {{ dollars(changeDue.cents) }}</span>
          <v-btn size="x-small" variant="flat" color="warning" @click="api.changeCollected()">Collect change</v-btn>
        </div>
      </v-alert>

      <div class="step">1. Grade <v-chip v-if="pump?.selectedGrade" color="success">{{ pump.selectedGrade }}</v-chip></div>
      <div class="row-wrap">
        <v-btn v-for="g in grades" :key="g" :disabled="!canGrade" :color="pump?.selectedGrade === g ? 'primary' : undefined"
               :variant="pump?.selectedGrade === g ? 'flat' : 'outlined'" @click="selectedGrade(g)">
          {{ g }} <span class="ml-1" :class="pump?.selectedGrade === g ? '' : 'muted'">{{ state?.prices[g] }}</span>
        </v-btn>
      </div>

      <div class="step">2. Nozzle
        <v-chip :color="nozzleOut ? 'info' : undefined">{{ nozzleOut ? 'off harness' : 'on harness' }}</v-chip>
        <v-chip :color="seated ? 'success' : undefined">{{ seated ? 'in tank' : 'not in tank' }}</v-chip>
      </div>
      <div class="row-wrap">
        <v-btn v-if="!nozzleOut" :disabled="!armed" prepend-icon="mdi-gas-station" @click="api.lift()">Lift nozzle</v-btn>
        <v-btn v-else prepend-icon="mdi-gas-station-off" @click="api.hang()">Hang nozzle{{ pump?.poured ? ' (ends sale)' : '' }}</v-btn>
        <v-btn v-if="!seated" :disabled="!armed || !nozzleOut" prepend-icon="mdi-car" @click="api.seat()">Put in tank</v-btn>
        <v-btn v-else prepend-icon="mdi-car-off" @click="api.unseat()">Pull out of tank</v-btn>
      </div>

      <div class="step">3. Trigger <v-chip :color="flowing ? 'success' : undefined">{{ flowing ? 'flowing' : 'closed' }}</v-chip></div>
      <div class="row-wrap">
        <!-- One button, two states: it reads what the next press will do. -->
        <v-btn v-if="flowing" color="warning" variant="flat" prepend-icon="mdi-pause" @click="api.release()">Release trigger (pause)</v-btn>
        <v-btn v-else color="primary" variant="flat" :disabled="!canSqueeze" prepend-icon="mdi-play" @click="api.squeeze()">Squeeze trigger{{ pump?.poured ? ' (resume)' : '' }}</v-btn>
        <v-btn :disabled="!state?.pending.length" prepend-icon="mdi-bank-transfer" @click="api.drain()">Drain queued to bank ({{ state?.pending.length ?? 0 }})</v-btn>
      </div>

      <p class="muted mt-4 mb-0">
        Credit asks the bank for the site's credit ceiling. Debit asks for the debit ceiling and gets the lesser of that and the bank's answer; use a
        Debit card for it. Cash sets the ceiling to the cent, no bank. Fuel flows only with a grade chosen, the nozzle in the tank, and the trigger held.
      </p>
    </v-card-text>
  </v-card>
</template>
