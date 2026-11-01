<script setup lang="ts">
import { onMounted, computed } from 'vue'
import { Splitpanes, Pane } from 'splitpanes'
import { connect, connected, state, basename } from './sim'
import PumpFace from './components/PumpFace.vue'
import DispenserControls from './components/DispenserControls.vue'
import PaymentPanel from './components/PaymentPanel.vue'
import StationPanel from './components/StationPanel.vue'
import RecordsPanel from './components/RecordsPanel.vue'
import LinkStrip from './components/LinkStrip.vue'

onMounted(connect)
const d = computed(() => state.value?.dispenser)
const st = computed(() => state.value?.station)
const dispenserTitle = computed(() => {
  if (!d.value) return 'DISPENSER'
  if (d.value.link === 'in-process') return `DISPENSER · in the station process`
  return d.value.connected ? `DISPENSER · GasPump.Dispenser.exe pid ${d.value.pid}` : 'DISPENSER · OFFLINE'
})
</script>

<template>
  <v-app>
    <v-app-bar density="compact" flat color="surface">
      <v-app-bar-title class="text-subtitle-1 font-weight-bold">Gas Pump Simulator</v-app-bar-title>
      <v-chip :color="connected ? 'success' : 'error'" class="mr-2">{{ connected ? 'station connected' : 'connecting to station' }}</v-chip>
      <v-chip v-if="state" color="info" class="mr-2">dispatch: {{ state.strategy }}</v-chip>
      <v-chip v-if="d" :color="d.link === 'socket' ? 'warning' : undefined" class="mr-4">{{ d.link === 'socket' ? 'two processes, TCP ' + d.port : 'one process' }}</v-chip>
      <span v-if="state" class="muted">hard limits, owned by the dispenser: {{ state.limits.maxGallons }} gal, ${{ state.limits.maxCents / 100 }}, {{ state.limits.maxMinutes }} min, {{ state.limits.maxGpm }} gpm</span>
      <v-spacer />
      <span class="muted mr-4">drag the gaps between panels to resize</span>
    </v-app-bar>

    <v-main>
      <div class="pa-3 workspace">
        <!-- Two columns and the wire between them. Nothing on the left came from the station process; nothing on the right came from the dispenser process. -->
        <Splitpanes class="pump-theme">
          <Pane :size="44" :min-size="26">
            <div class="side dispenser">
              <div class="side-head">
                <v-icon size="18">mdi-gas-station</v-icon>
                <span class="side-title">{{ dispenserTitle }}</span>
                <span v-if="d && d.link === 'socket'" class="muted">{{ state?.pumpId }} · run {{ d.runId ?? '-' }} · chain {{ basename(d.custodyPath) || '-' }}</span>
                <span v-else class="muted">{{ state?.pumpId }}</span>
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
                <span class="muted">http {{ st?.port ?? 5177 }} · journal run {{ st?.runId ?? '-' }} · chain {{ basename(st?.custodyPath) || '-' }}</span>
              </div>
              <Splitpanes class="pump-theme" horizontal>
                <Pane :size="36" :min-size="18"><PaymentPanel /></Pane>
                <Pane :size="36" :min-size="16"><StationPanel /></Pane>
                <Pane :size="28" :min-size="12"><RecordsPanel origin="station" /></Pane>
              </Splitpanes>
            </div>
          </Pane>
        </Splitpanes>
      </div>
    </v-main>
  </v-app>
</template>
