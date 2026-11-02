<script setup lang="ts">
import { ref, computed } from 'vue'
import { api, cards, state, dollars, pumpState } from '../sim'

// The station's side of a sale: who pays, how much the site will hold, and the attacks that come from
// upstream (a signed token, a tampered one, a replay). None of this touches the valve directly; the
// station can only hand the dispenser a token and wait.
const tender = ref<'Credit' | 'Debit' | 'Cash'>('Credit')
const card = ref('Good')
const cashDollars = ref(20)
const refused = ref<string | null>(null)
const gallons = ref(600)
const dollarsWanted = ref(2000)
const sale = computed(() => pumpState.value?.sale)
const s = computed(() => sale.value?.state)
const estop = computed(() => !!state.value?.station.estopActive)
const canCancel = computed(() => s.value === 'Authorizing' || s.value === 'ReadyToPump')
const cardItems = computed(() => cards.value.map(c => ({ title: `${c.name} · ${c.kind} ····${c.last4} exp ${c.expiry}`, value: c.name })))
const changeDue = computed(() => state.value?.cash.changeDue.find(c => c.saleId === sale.value?.saleId))
const siteCeiling = computed(() => state.value && tender.value !== 'Cash' ? dollars(tender.value === 'Debit' ? state.value.site.debitHoldCents : state.value.site.creditHoldCents) : null)
const holdText: Record<string, string> = {
  None: 'no hold', Requested: 'hold requested', Held: 'hold on card', Settled: 'charged', Released: 'hold released',
  SettlePending: 'charge queued', ReleasePending: 'release queued',
}

async function start() {
  refused.value = null
  if (tender.value === 'Cash') { await api.payCash(Math.round(cashDollars.value * 100)); return }
  const r = await api.presentCard(card.value, tender.value, 'Station')
  if (!r.ok) refused.value = r.error ?? 'refused'
}
</script>

<template>
  <v-card class="pane-card">
    <v-card-title>Point of sale</v-card-title>
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
        <v-btn color="primary" variant="flat" :disabled="estop" @click="start">
          {{ tender === 'Cash' ? `Pay cashier ${dollars(Math.round(cashDollars * 100))}` : 'Card at the counter' }}
        </v-btn>
        <v-btn :disabled="!canCancel" @click="api.cancel()">Cashier cancels</v-btn>
        <v-btn variant="text" @click="api.reset()">New sale</v-btn>
        <span v-if="refused" class="text-error" style="font-size: 12px">{{ refused }}</span>
      </div>
      <p class="muted mb-2">A card or cash at the counter sets the pump with a {{ 300 }} s wait for the lift, because the cashier can cancel by hand. A card at the pump's own reader (left column) gets {{ 120 }} s.</p>
      <v-alert v-if="changeDue" type="warning" class="mb-2">
        <div class="d-flex align-center ga-3">
          <span>Cashier owes you {{ dollars(changeDue.cents) }}</span>
          <v-btn size="x-small" variant="flat" color="warning" @click="api.changeCollected()">Collect change</v-btn>
        </div>
      </v-alert>

      <div class="kv mb-2">
        <b>sale</b><span>{{ sale?.saleId }} {{ sale?.cardName ?? '' }} · {{ sale?.state }} · {{ sale?.tender?.toLowerCase() }}: {{ holdText[sale?.hold ?? 'None'] }}</span>
        <b>bank owes</b>
        <span :class="{ 'text-warning': state?.pending.length }">
          <template v-if="state?.pending.length">{{ state.pending.map(p => `${p.kind} ${p.saleId}${p.cents ? ' ' + dollars(p.cents) : ''}`).join(', ') }} (queued)</template>
          <template v-else>nothing queued</template>
        </span>
        <b>open holds</b><span :class="{ 'text-info': state?.openHolds.length }">{{ state?.openHolds.length ? state.openHolds.join(', ') : 'none at the bank' }}</span>
      </div>
      <div class="row-wrap mb-2">
        <v-btn :disabled="!state?.pending.length" size="small" prepend-icon="mdi-bank-transfer" @click="api.drain()">Drain queued to bank ({{ state?.pending.length ?? 0 }})</v-btn>
      </div>

      <div class="step">Attack from upstream</div>
      <div class="row-wrap mb-2">
        <v-btn color="error" size="small" prepend-icon="mdi-key-alert" @click="api.attack('overlimit', { gallons, cents: dollarsWanted * 100 })">Signed token: {{ gallons }} gal, ${{ dollarsWanted }}</v-btn>
        <v-btn color="error" size="small" prepend-icon="mdi-pencil-off" @click="api.attack('tamper', { cents: 200000 })">Tamper ceiling to $2000</v-btn>
        <v-btn color="error" size="small" prepend-icon="mdi-repeat" @click="api.attack('replay')">Replay last token</v-btn>
        <v-btn color="error" size="small" prepend-icon="mdi-key-remove" @click="api.attack('notoken')">Arm with no token</v-btn>
        <v-text-field v-model.number="gallons" type="number" label="gallons" density="compact" style="max-width: 110px" />
        <v-text-field v-model.number="dollarsWanted" type="number" label="dollars" density="compact" style="max-width: 110px" />
      </div>
      <p class="muted mb-0">
        The signed token is the Detroit case: the station is fully owned and signs for 600 gallons. The dispenser, in its own process,
        clamps it to its own hard limits. Watch the left column's ledger for <code>arm.refused</code> and the clamp note.
      </p>
    </v-card-text>
  </v-card>
</template>
