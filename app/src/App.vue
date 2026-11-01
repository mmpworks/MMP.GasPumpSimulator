<script setup lang="ts">
import { onMounted } from 'vue'
import { Splitpanes, Pane } from 'splitpanes'
import 'splitpanes/dist/splitpanes.css'
import { connect, connected, state } from './sim'
import PumpFace from './components/PumpFace.vue'
import ControlPanel from './components/ControlPanel.vue'
import AttackPanel from './components/AttackPanel.vue'
import TabsPanel from './components/TabsPanel.vue'
import LogPanel from './components/LogPanel.vue'

onMounted(connect)
</script>

<template>
  <div class="topbar">
    <h1>Gas Pump Simulator</h1>
    <span class="badge" :class="connected ? 'ok' : 'bad'">{{ connected ? 'host connected' : 'connecting to host' }}</span>
    <span v-if="state" class="badge info">dispatch: {{ state.strategy }}</span>
    <span v-if="state" class="badge">{{ state.pumpId }}</span>
    <span v-if="state" class="badge" :class="state.ledgerOk ? 'ok' : 'bad'">ledger {{ state.ledgerOk ? 'intact' : 'BROKEN' }}</span>
    <span v-if="state" class="muted">hard limits: {{ state.limits.maxGallons }} gal, ${{ state.limits.maxCents / 100 }}, {{ state.limits.maxMinutes }} min, {{ state.limits.maxGpm }} gpm</span>
    <span class="muted" style="margin-left: auto">drag the gaps between panels to resize</span>
  </div>

  <!-- Every gap is a splitter. Outer: top row over the log. Top row: dispenser | driver over attack | tabs. -->
  <Splitpanes class="dark-theme workspace" horizontal :push-other-panes="false">
    <Pane :size="72" :min-size="30">
      <Splitpanes class="dark-theme" :push-other-panes="false">
        <Pane :size="38" :min-size="22"><PumpFace class="panel fill" /></Pane>
        <Pane :size="31" :min-size="22">
          <Splitpanes class="dark-theme" horizontal :push-other-panes="false">
            <Pane :size="64" :min-size="25"><ControlPanel class="panel fill" /></Pane>
            <Pane :size="36" :min-size="15"><AttackPanel class="panel fill" /></Pane>
          </Splitpanes>
        </Pane>
        <Pane :size="31" :min-size="22"><TabsPanel class="panel fill" /></Pane>
      </Splitpanes>
    </Pane>
    <Pane :size="28" :min-size="10"><LogPanel class="panel fill log" /></Pane>
  </Splitpanes>
</template>
