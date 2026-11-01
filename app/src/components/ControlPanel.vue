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
const cardList = computed(() => cards.value.filter(c => tender.value === 'Debit' ? true : true))
const changeDue = computed(() => state.value?.cash.changeDue.find(c => c.saleId === sale.value?.saleId))

function start() {
  if (tender.value === 'Cash') api.payCash(Math.round(cashDollars.value * 100))
  else api.presentCard(card.value, tender.value)
}
</script>

<template>
  <div>
    <h2>Driver</h2>
    <div class="controls" style="margin-bottom: 6px">
      <span class="muted">pay with</span>
      <button v-for="t in ['Credit', 'Debit', 'Cash'] as const" :key="t" :class="{ primary: tender === t }" @click="tender = t">{{ t }}</button>
      <span class="muted" v-if="state && tender !== 'Cash'">site ceiling {{ dollars(tender === 'Debit' ? state.site.debitHoldCents : state.site.creditHoldCents) }}</span>
    </div>
    <div class="controls" style="margin-bottom: 10px">
      <template v-if="tender !== 'Cash'">
        <select v-model="card" title="Each card is named after what it will do">
          <option v-for="c in cardList" :key="c.name" :value="c.name">{{ c.name }} · {{ c.kind }} ····{{ c.last4 }} exp {{ c.expiry }}</option>
        </select>
        <button class="primary" :disabled="!canPresent" @click="start">Present card</button>
      </template>
      <template v-else>
        <label class="muted">cash to cashier $</label>
        <input type="number" v-model.number="cashDollars" min="1" step="5" style="width: 90px" />
        <button class="primary" :disabled="!canPresent" @click="start">Pay cashier {{ dollars(Math.round(cashDollars * 100)) }}</button>
      </template>
      <button :disabled="!canCancel" @click="api.cancel()">Cancel</button>
      <button @click="api.reset()">New sale</button>
    </div>
    <div v-if="changeDue" class="controls" style="margin-bottom: 10px">
      <span class="badge warn">cashier owes you {{ dollars(changeDue.cents) }}</span>
      <button @click="api.changeCollected()">Collect change</button>
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
      <button v-else @click="api.hang()">Hang nozzle{{ pump?.poured ? ' (ends sale)' : '' }}</button>
      <button v-if="!seated" :disabled="!armed || !nozzleOut" @click="api.seat()">Put in tank</button>
      <button v-else @click="api.unseat()">Pull out of tank</button>
    </div>

    <div class="muted" style="margin-bottom: 4px">3. Trigger <span class="badge" :class="flowing ? 'ok' : ''">{{ flowing ? 'flowing' : 'closed' }}</span></div>
    <div class="grid2" style="margin-bottom: 10px">
      <button class="primary" :disabled="!canSqueeze" @click="api.squeeze()">Squeeze trigger</button>
      <button :disabled="!flowing" @click="api.release()">Release trigger</button>
    </div>

    <button :disabled="!state?.pending.length" @click="api.drain()">Drain queued to bank ({{ state?.pending.length ?? 0 }})</button>
    <p class="muted" style="margin: 10px 0 0">
      Credit asks the bank for the site's credit ceiling. Debit asks for the debit ceiling and gets the lesser of that and the bank's answer; use a
      Debit card for it. Cash sets the ceiling to the cent, no bank. Fuel flows only with a grade chosen, the nozzle in the tank, and the trigger held.
    </p>
  </div>
</template>
