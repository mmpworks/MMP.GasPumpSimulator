<script setup lang="ts">
import { onMounted } from 'vue'
import { connect, connected, state } from './sim'
import PumpFace from './components/PumpFace.vue'
import ControlPanel from './components/ControlPanel.vue'
import NetworkPanel from './components/NetworkPanel.vue'
import AttackPanel from './components/AttackPanel.vue'
import LedgerPanel from './components/LedgerPanel.vue'
import LogPanel from './components/LogPanel.vue'
import AdminPanel from './components/AdminPanel.vue'

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
  </div>
  <PumpFace class="panel pump" />
  <ControlPanel class="panel" />
  <NetworkPanel class="panel" />
  <AttackPanel class="panel" />
  <LedgerPanel class="panel ledger" />
  <AdminPanel class="panel" style="grid-column: 1 / -1" />
  <LogPanel class="panel log" style="grid-column: 1 / -1" />
</template>
