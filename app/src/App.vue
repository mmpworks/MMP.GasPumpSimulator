<script setup lang="ts">
import { onMounted, computed, watch } from 'vue'
import { Splitpanes, Pane } from 'splitpanes'
import { connect, connected, state, basename, activePump, selectPump, dollars, type PumpEntry } from './sim'
import { applyTheme } from './theme'
import PumpFace from './components/PumpFace.vue'
import DispenserControls from './components/DispenserControls.vue'
import PaymentPanel from './components/PaymentPanel.vue'
import StationPanel from './components/StationPanel.vue'
import RecordsPanel from './components/RecordsPanel.vue'
import LinkStrip from './components/LinkStrip.vue'

onMounted(connect)
const st = computed(() => state.value?.station)
const pumps = computed(() => state.value?.pumps ?? [])
const stationName = computed(() => st.value?.name ?? 'Gas Pump Simulator')
// The host names the station and its theme. The window title, the stylesheet, and Vuetify follow it.
watch(() => st.value?.theme, theme => { if (theme) applyTheme(theme) }, { immediate: true })
watch(stationName, name => { document.title = `${name} · Gas Pump Simulator` }, { immediate: true })

// One tab per pump. The tab's label carries the pump's live state, so the operator sees the other pump without switching.
function dispenserTitle(p: PumpEntry) {
  const d = p.dispenser
  if (d.link === 'in-process') return `DISPENSER ${p.number} · in the station process`
  return d.connected ? `DISPENSER ${p.number} · GasPump.Dispenser.exe pid ${d.pid}` : `DISPENSER ${p.number} · OFFLINE`
}
function tabHint(p: PumpEntry) {
  if (!p.pump.inService) return 'out of service'
  const s = p.sale
  if (s.state === 'Fueling') return `fueling ${dollars(s.cents)}`
  if (s.state === 'Idle') return p.pump.phase === 'Calling' ? 'calling' : 'idle'
  return s.state.toLowerCase()
}
function tabColor(p: PumpEntry) {
  if (!p.dispenser.connected || !p.pump.inService) return 'error'
  if (p.sale.state === 'Fueling') return 'success'
  if (p.sale.state === 'Idle') return undefined
  return 'info'
}
</script>

<template>
  <v-app>
    <v-app-bar density="compact" flat color="surface">
      <v-app-bar-title class="text-subtitle-1 font-weight-bold" data-testid="station-name">{{ stationName }}</v-app-bar-title>
      <v-chip :color="connected ? 'success' : 'error'" class="mr-2">{{ connected ? 'station connected' : 'connecting to station' }}</v-chip>
      <v-chip v-if="state" color="info" class="mr-2">dispatch: {{ state.strategy }}</v-chip>
      <v-chip v-if="pumps.length" :color="pumps[0].dispenser.link === 'socket' ? 'warning' : undefined" class="mr-4">
        {{ pumps[0].dispenser.link === 'socket' ? `${pumps.length + 1} processes, TCP ${pumps.map(p => p.dispenser.port).join(' + ')}` : 'one process' }}
      </v-chip>
      <span v-if="state" class="muted">hard limits, owned by each dispenser: {{ state.limits.maxGallons }} gal, ${{ state.limits.maxCents / 100 }}, {{ state.limits.maxMinutes }} min, {{ state.limits.maxGpm }} gpm</span>
      <v-spacer />
      <span class="muted mr-4">drag the gaps between panels to resize</span>
    </v-app-bar>

    <v-main>
      <div class="pa-3 workspace">
        <!-- The forecourt controller drives every pump; the window shows one at a time. The tab strip is the forecourt: every pump's state at a glance. -->
        <v-tabs :model-value="activePump" color="primary" density="comfortable" class="pump-tabs mb-2" @update:model-value="v => selectPump(Number(v))">
          <v-tab v-for="p in pumps" :key="p.number" :value="p.number" class="text-none" :data-testid="`pump-tab-${p.number}`">
            <v-icon size="18" class="mr-1">mdi-gas-station</v-icon>
            Pump {{ p.number }}
            <v-chip size="x-small" class="ml-2" :color="tabColor(p)" variant="flat" :data-testid="`pump-tab-hint-${p.number}`">{{ tabHint(p) }}</v-chip>
          </v-tab>
        </v-tabs>

        <!-- Two columns and the wire between them, for the pump on the open tab. Nothing on the left came from the station process; nothing on the right came from a dispenser process. -->
        <template v-for="p in pumps" :key="p.number">
          <Splitpanes v-if="p.number === activePump" class="pump-theme pump-view" :data-pump="p.number">
            <Pane :size="44" :min-size="26">
              <div class="side dispenser">
                <div class="side-head">
                  <v-icon size="18">mdi-gas-station</v-icon>
                  <span class="side-title">{{ dispenserTitle(p) }}</span>
                  <span v-if="p.dispenser.link === 'socket'" class="muted">{{ p.pumpId }} · node {{ p.node }} · run {{ p.dispenser.runId ?? '-' }} · chain {{ basename(p.dispenser.custodyPath) || '-' }}</span>
                  <span v-else class="muted">{{ p.pumpId }} · node {{ p.node }}</span>
                </div>
                <Splitpanes class="pump-theme" horizontal>
                  <Pane :size="44" :min-size="24"><PumpFace /></Pane>
                  <Pane :size="30" :min-size="16"><DispenserControls /></Pane>
                  <Pane :size="26" :min-size="12"><RecordsPanel origin="dispenser" /></Pane>
                </Splitpanes>
              </div>
            </Pane>
            <!-- Panes push each other: a column that grew can shrink again, because the far column gives the width back. -->
            <Pane :size="10" :min-size="6"><LinkStrip /></Pane>
            <Pane :size="46" :min-size="26">
              <div class="side station">
                <div class="side-head">
                  <v-icon size="18">mdi-server</v-icon>
                  <span class="side-title">STATION · GasPump.Host.exe pid {{ st?.pid ?? '-' }}</span>
                  <span class="muted">http {{ st?.port ?? 5177 }} · journal run {{ st?.runId ?? '-' }} · chain {{ basename(st?.custodyPath) || '-' }} · {{ pumps.length }} pumps on this controller</span>
                </div>
                <Splitpanes class="pump-theme" horizontal>
                  <Pane :size="36" :min-size="18"><PaymentPanel /></Pane>
                  <Pane :size="36" :min-size="16"><StationPanel /></Pane>
                  <Pane :size="28" :min-size="12"><RecordsPanel origin="station" /></Pane>
                </Splitpanes>
              </div>
            </Pane>
          </Splitpanes>
        </template>
      </div>
    </v-main>
  </v-app>
</template>
