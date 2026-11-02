<script setup lang="ts">
import { computed, ref } from 'vue'
import { api, cards, state, pumpState } from '../sim'

// Controls for what a hand can do at the pump and what can go wrong on the island. The card reader
// (insert or tap) is here too. Everything else becomes a PumpMessage down the link.
const sale = computed(() => pumpState.value?.sale)
const readerCard = ref('Good')
const readerTender = ref<'Credit' | 'Debit'>('Credit')
const readerNote = ref<string | null>(null)
// The keypad shows while the sale waits for a PIN (debit) or a billing ZIP (credit). The test bank knows ZIP 12345 and PIN 1234.
const keypad = ref('')
const wantsCode = computed(() => sale.value?.state === 'CardRead')
const codeLabel = computed(() => (sale.value?.tender === 'Debit' ? 'PIN (4 digits)' : 'billing ZIP (5 digits)'))
const codeLength = computed(() => (sale.value?.tender === 'Debit' ? 4 : 5))
async function sendCode() {
  const code = keypad.value
  keypad.value = ''
  await api.enterCode(code)
}
const cardItems = computed(() => cards.value.map(c => ({ title: `${c.name} · ${c.kind} ····${c.last4}`, value: c.name })))
async function readCard(source: 'PumpInsert' | 'PumpTap') {
  const r = await api.presentCard(readerCard.value, readerTender.value, source)
  readerNote.value = r.ok ? null : r.error ?? 'refused'
  if (r.ok) setTimeout(() => (readerNote.value = null), 0)
}
const outOfService = computed(() => !!pump.value && !pump.value.inService)
const whyNoSqueeze = computed(() => {
  if (offline.value) return 'dispenser offline'
  if (outOfService.value) return `out of service: ${pump.value?.serviceParts.join(', ')}`
  if (!armed.value) return 'no authorization yet'
  if (!pump.value?.selectedGrade) return 'select a grade first'
  if (!nozzleOut.value) return 'lift the nozzle first'
  if (!spoutIn.value) return 'insert the spout first'
  return ''
})
const pump = computed(() => pumpState.value?.pump)
const d = computed(() => pumpState.value?.dispenser)
const s = computed(() => sale.value?.state)
const offline = computed(() => !d.value?.connected)
const armed = computed(() => s.value === 'ReadyToPump' || s.value === 'Fueling')
const canGrade = computed(() => s.value === 'ReadyToPump' && !offline.value)
const nozzleOut = computed(() => !!pump.value?.nozzleLifted)
const spoutIn = computed(() => !!pump.value?.spoutInserted)
const flowing = computed(() => pump.value?.phase === 'Dispensing')
const canSqueeze = computed(() => armed.value && !flowing.value && nozzleOut.value && spoutIn.value && !!pump.value?.selectedGrade && !offline.value)
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
        <span v-if="readerNote" class="text-error reader-note" style="font-size: 12px">{{ readerNote }}</span>
        <span v-else-if="outOfService" class="text-error" style="font-size: 12px">reader closed: {{ pump?.serviceParts.join(', ') }}</span>
      </div>
      <div v-if="wantsCode" class="row-wrap mb-1 keypad">
        <v-text-field v-model="keypad" :label="codeLabel" :maxlength="codeLength" inputmode="numeric" density="compact" hide-details
                      :type="sale?.tender === 'Debit' ? 'password' : 'text'" style="max-width: 220px" @keyup.enter="sendCode" />
        <v-btn color="primary" variant="flat" size="small" prepend-icon="mdi-dialpad" :disabled="keypad.length !== codeLength" @click="sendCode">Enter</v-btn>
      </div>
      <p class="muted mb-1">A card here cancels an approved sale nobody touched (the last driver left) and starts yours. While a nozzle is in someone's hand, the reader says the pump is in use. The lift wait is {{ 120 }} s here, {{ 300 }} s for a sale set at the station.</p>

      <div class="step">1. Grade <v-chip v-if="pump?.selectedGrade" color="success">{{ pump.selectedGrade }}</v-chip></div>
      <div class="row-wrap">
        <v-btn v-for="g in grades" :key="g" :disabled="!canGrade" :color="pump?.selectedGrade === g ? 'primary' : undefined"
               :variant="pump?.selectedGrade === g ? 'flat' : 'outlined'" @click="selectedGrade(g)">
          {{ g }} <span class="ml-1" :class="pump?.selectedGrade === g ? '' : 'muted'">{{ pumpState?.prices[g] }}</span>
        </v-btn>
      </div>

      <div class="step">2. Nozzle
        <v-chip :color="nozzleOut ? 'info' : undefined">{{ nozzleOut ? 'out of boot' : 'in boot' }}</v-chip>
        <v-chip :color="spoutIn ? 'success' : undefined">{{ spoutIn ? 'spout in fill pipe' : 'spout out' }}</v-chip>
      </div>
      <div class="row-wrap">
        <v-btn v-if="!nozzleOut" :disabled="offline || outOfService" prepend-icon="mdi-gas-station" @click="api.lift()">Lift nozzle{{ armed ? '' : ' (calls for authorization)' }}</v-btn>
        <v-btn v-else :disabled="offline" prepend-icon="mdi-gas-station-off" @click="api.hang()">Hang nozzle{{ pump?.poured ? ' (ends sale)' : '' }}</v-btn>
        <v-btn v-if="!spoutIn" :disabled="!armed || !nozzleOut || offline" prepend-icon="mdi-car" @click="api.insertSpout()">Insert spout</v-btn>
        <v-btn v-else :disabled="offline" prepend-icon="mdi-car-off" @click="api.withdrawSpout()">Withdraw spout</v-btn>
      </div>

      <div class="step">3. Lever <v-chip :color="flowing ? 'success' : undefined">{{ flowing ? 'flowing' : 'closed' }}</v-chip></div>
      <div class="row-wrap">
        <v-btn v-if="flowing" color="warning" variant="flat" prepend-icon="mdi-pause" @click="api.release()">Release lever (pause)</v-btn>
        <v-btn v-else color="primary" variant="flat" :disabled="!canSqueeze" prepend-icon="mdi-play" @click="api.squeeze()">Squeeze lever{{ pump?.poured ? ' (resume)' : '' }}</v-btn>
        <span v-if="whyNoSqueeze" class="muted">{{ whyNoSqueeze }}</span>
      </div>

      <div class="step">Forecourt faults</div>
      <div class="row-wrap mb-1">
        <v-btn v-if="pump?.supplyAvailable !== false" color="warning" size="small" prepend-icon="mdi-gas-station-off" :disabled="estop || offline" @click="api.tank('tankout')">Tank runs dry</v-btn>
        <v-btn v-else color="success" size="small" prepend-icon="mdi-gas-station" :disabled="offline" @click="api.tank('tankback')">Tank refilled</v-btn>
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
        Fuel flows only with a grade chosen, the spout in the fill pipe, and the lever held. Each fault names its part with a code
        (<code>PUMP.STP.NOFLOW</code>, <code>PUMP.MTR.NOPULSE</code>, <code>PUMP.HOSE.BREAKAWAY</code>) in this column's log, ledger, and chain.
      </p>
    </v-card-text>
  </v-card>
</template>
