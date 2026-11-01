<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, cards, state } from '../sim'

// What a hand can do at the pump, and what can go wrong on the island. The card reader is here too:
// insert or tap at the pump. Everything else becomes a PumpMessage down the link.
const sale = computed(() => state.value?.sale)
const readerCard = ref('Good')
const readerTender = ref<'Credit' | 'Debit'>('Credit')
const readerNote = ref<string | null>(null)
const cardItems = computed(() => cards.value.map(c => ({ title: `${c.name} · ${c.kind} ····${c.last4}`, value: c.name })))
async function readCard(source: 'PumpInsert' | 'PumpTap') {
  const r = await api.presentCard(readerCard.value, readerTender.value, source)
  readerNote.value = r.ok ? null : r.error ?? 'refused'
  if (r.ok) setTimeout(() => (readerNote.value = null), 0)
}
const whyNoSqueeze = computed(() => {
  if (offline.value) return 'dispenser offline'
  if (!armed.value) return 'no authorization yet'
  if (!pump.value?.selectedGrade) return 'select a grade first'
  if (!nozzleOut.value) return 'lift the nozzle first'
  if (!seated.value) return 'put the nozzle in the tank first'
  return ''
})
const pump = computed(() => state.value?.pump)
const d = computed(() => state.value?.dispenser)
const s = computed(() => sale.value?.state)
const offline = computed(() => !d.value?.connected)
const armed = computed(() => s.value === 'ReadyToPump' || s.value === 'Fueling')
const canGrade = computed(() => s.value === 'ReadyToPump' && !offline.value)
const nozzleOut = computed(() => !!pump.value?.nozzleLifted)
const seated = computed(() => !!pump.value?.nozzleSeated)
const flowing = computed(() => pump.value?.phase === 'Dispensing')
const canSqueeze = computed(() => armed.value && !flowing.value && nozzleOut.value && seated.value && !!pump.value?.selectedGrade && !offline.value)
const grades = ['Regular', 'Plus', 'Premium']
function selectedGrade(g: string | null) { if (g) api.selectGrade(g) }
const estop = computed(() => !!state.value?.station.estopActive)
</script>

<template>
  <v-card class="pane-card">
    <v-card-title>At the pump</v-card-title>
    <v-card-text>
      <v-alert v-if="offline && !estop" type="error" density="compact" variant="tonal" class="mb-2">
        The dispenser process is not reachable. The station shows its last known state; nothing here can move fuel.
        <v-btn v-if="d && d.link === 'socket' && !d.external" size="small" variant="flat" color="error" class="ml-3" prepend-icon="mdi-power" @click="api.dispenserPowerOn()">Restore dispenser power</v-btn>
      </v-alert>

      <div class="step">Card reader <v-chip v-if="sale && sale.state !== 'Idle' && sale.source !== 'Station'" size="small" color="info">{{ sale.source === 'PumpTap' ? 'tapped here' : 'inserted here' }}</v-chip></div>
      <div class="row-wrap mb-1">
        <v-select v-model="readerCard" :items="cardItems" label="card" density="compact" style="min-width: 220px; max-width: 300px" />
        <v-btn-toggle v-model="readerTender" mandatory density="compact" variant="outlined">
          <v-btn value="Credit" size="small">Credit</v-btn>
          <v-btn value="Debit" size="small">Debit</v-btn>
        </v-btn-toggle>
        <v-btn color="primary" variant="flat" size="small" prepend-icon="mdi-credit-card-outline" :disabled="offline || estop" @click="readCard('PumpInsert')">Insert</v-btn>
        <v-btn color="primary" variant="flat" size="small" prepend-icon="mdi-contactless-payment" :disabled="offline || estop" @click="readCard('PumpTap')">Tap</v-btn>
        <span v-if="readerNote" class="text-error" style="font-size: 12px">{{ readerNote }}</span>
      </div>
      <p class="muted mb-1">A card here cancels an approved sale nobody touched (the last driver left) and starts yours. While a nozzle is in someone's hand, the reader says the pump is in use. The lift wait is {{ 120 }} s here, {{ 300 }} s for a sale set at the station.</p>

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
        <v-btn v-if="!nozzleOut" :disabled="!armed || offline" prepend-icon="mdi-gas-station" @click="api.lift()">Lift nozzle</v-btn>
        <v-btn v-else :disabled="offline" prepend-icon="mdi-gas-station-off" @click="api.hang()">Hang nozzle{{ pump?.poured ? ' (ends sale)' : '' }}</v-btn>
        <v-btn v-if="!seated" :disabled="!armed || !nozzleOut || offline" prepend-icon="mdi-car" @click="api.seat()">Put in tank</v-btn>
        <v-btn v-else :disabled="offline" prepend-icon="mdi-car-off" @click="api.unseat()">Pull out of tank</v-btn>
      </div>

      <div class="step">3. Trigger <v-chip :color="flowing ? 'success' : undefined">{{ flowing ? 'flowing' : 'closed' }}</v-chip></div>
      <div class="row-wrap">
        <v-btn v-if="flowing" color="warning" variant="flat" prepend-icon="mdi-pause" @click="api.release()">Release trigger (pause)</v-btn>
        <v-btn v-else color="primary" variant="flat" :disabled="!canSqueeze" prepend-icon="mdi-play" @click="api.squeeze()">Squeeze trigger{{ pump?.poured ? ' (resume)' : '' }}</v-btn>
        <span v-if="whyNoSqueeze" class="muted">{{ whyNoSqueeze }}</span>
      </div>

      <div class="step">Forecourt faults</div>
      <div class="row-wrap mb-1">
        <v-btn v-if="pump?.supplyAvailable !== false" color="warning" size="small" prepend-icon="mdi-gas-station-off" :disabled="estop || offline" @click="api.fault('tankout')">Tank runs dry</v-btn>
        <v-btn v-else color="success" size="small" prepend-icon="mdi-gas-station" :disabled="offline" @click="api.fault('tankback')">Tank refilled</v-btn>
        <v-btn v-if="!pump?.pulserDead" color="warning" size="small" prepend-icon="mdi-counter" :disabled="estop || offline" @click="api.fault('pulserdead')">Kill the pulser</v-btn>
        <v-btn v-else color="success" size="small" prepend-icon="mdi-counter" :disabled="offline" @click="api.fault('pulserfix')">Fix the pulser</v-btn>
        <v-btn v-if="!pump?.breakawayOpen" color="warning" size="small" prepend-icon="mdi-car-off" :disabled="estop || offline" @click="api.fault('driveoff')">Drive-off (breakaway)</v-btn>
        <v-btn v-else color="success" size="small" prepend-icon="mdi-wrench" :disabled="offline" @click="api.fault('breakawayreset')">Attendant resets the breakaway</v-btn>
        <v-btn color="error" size="small" variant="outlined" prepend-icon="mdi-pulse" :disabled="offline" @click="api.attack('spoof', { pulses: 2000 })">Spoof 2000 pulses</v-btn>
      </div>
      <div v-if="d && d.link === 'socket'" class="row-wrap mb-1">
        <v-btn v-if="d.connected" color="error" size="small" prepend-icon="mdi-power-plug-off" :disabled="d.external" :title="d.external ? 'this dispenser was started by hand; stop it from its own terminal' : ''" @click="api.dispenserPowerLoss()">Pull the dispenser's plug</v-btn>
        <span class="muted">Kills GasPump.Dispenser.exe mid-whatever. The station faults the sale with <code>LINK.DISPENSER.LOST</code> and settles for the fuel it recorded.</span>
      </div>
      <p class="muted mb-0">
        Fuel flows only with a grade chosen, the nozzle in the tank, and the trigger held. Each fault names its part with a code
        (<code>PUMP.STP.NOFLOW</code>, <code>PUMP.MTR.NOPULSE</code>, <code>PUMP.HOSE.BREAKAWAY</code>) in this column's log, ledger, and chain.
      </p>
    </v-card-text>
  </v-card>
</template>
