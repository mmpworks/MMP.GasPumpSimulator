<script setup lang="ts">
import { onMounted } from 'vue'
import { Splitpanes, Pane } from 'splitpanes'
import { connect, connected, state } from './sim'
import PumpFace from './components/PumpFace.vue'
import ControlPanel from './components/ControlPanel.vue'
import AttackPanel from './components/AttackPanel.vue'
import TabsPanel from './components/TabsPanel.vue'
import LogPanel from './components/LogPanel.vue'

onMounted(connect)
</script>

<template>
  <v-app>
    <v-app-bar density="compact" flat color="surface">
      <v-app-bar-title class="text-subtitle-1 font-weight-bold">Gas Pump Simulator</v-app-bar-title>
      <v-chip :color="connected ? 'success' : 'error'" class="mr-2">{{ connected ? 'host connected' : 'connecting to host' }}</v-chip>
      <v-chip v-if="state" color="info" class="mr-2">dispatch: {{ state.strategy }}</v-chip>
      <v-chip v-if="state" class="mr-2">{{ state.pumpId }}</v-chip>
      <v-chip v-if="state" :color="state.ledgerOk ? 'success' : 'error'" class="mr-4">ledger {{ state.ledgerOk ? 'intact' : 'BROKEN' }}</v-chip>
      <span v-if="state" class="muted">hard limits: {{ state.limits.maxGallons }} gal, ${{ state.limits.maxCents / 100 }}, {{ state.limits.maxMinutes }} min, {{ state.limits.maxGpm }} gpm</span>
      <v-spacer />
      <span class="muted mr-4">drag the gaps between panels to resize</span>
    </v-app-bar>

    <v-main>
      <div class="pa-3 workspace">
        <!-- Every gap is a splitter. Outer: top row over the log. Top row: dispenser | driver over attack | tabs. -->
        <Splitpanes class="pump-theme" horizontal :push-other-panes="false">
          <Pane :size="70" :min-size="30">
            <Splitpanes class="pump-theme" :push-other-panes="false">
              <Pane :size="36" :min-size="24"><PumpFace /></Pane>
              <Pane :size="32" :min-size="24">
                <Splitpanes class="pump-theme" horizontal :push-other-panes="false">
                  <Pane :size="66" :min-size="30"><ControlPanel /></Pane>
                  <Pane :size="34" :min-size="15"><AttackPanel /></Pane>
                </Splitpanes>
              </Pane>
              <Pane :size="32" :min-size="24"><TabsPanel /></Pane>
            </Splitpanes>
          </Pane>
          <Pane :size="30" :min-size="10"><LogPanel /></Pane>
        </Splitpanes>
      </div>
    </v-main>
  </v-app>
</template>
