<script setup lang="ts">
import { ref, computed } from 'vue'
import { state } from '../sim'
import NetworkPanel from './NetworkPanel.vue'
import LedgerPanel from './LedgerPanel.vue'
import AdminPanel from './AdminPanel.vue'

type Tab = 'host' | 'ledger' | 'admin'
const tab = ref<Tab>('host')
try { const saved = localStorage.getItem('gaspump.tab') as Tab | null; if (saved) tab.value = saved } catch { /* private window */ }
function pick(t: Tab) { tab.value = t; try { localStorage.setItem('gaspump.tab', t) } catch { /* ignore */ } }

// A small live hint on each tab so a change elsewhere is visible without switching.
const hostHint = computed(() => state.value ? state.value.network.mode : '')
const ledgerHint = computed(() => state.value ? (state.value.ledgerOk ? 'intact' : 'BROKEN') : '')
const adminHint = computed(() => state.value?.site.lockedUntil ? 'LOCKED' : '')
</script>

<template>
  <div>
    <div class="tabs">
      <button :class="{ active: tab === 'host' }" @click="pick('host')">Host link <span class="muted">{{ hostHint }}</span></button>
      <button :class="{ active: tab === 'ledger' }" @click="pick('ledger')">Ledger <span class="muted" :class="{ bad: ledgerHint === 'BROKEN' }">{{ ledgerHint }}</span></button>
      <button :class="{ active: tab === 'admin' }" @click="pick('admin')">Administrator <span class="bad">{{ adminHint }}</span></button>
    </div>
    <div class="tabbody">
      <NetworkPanel v-show="tab === 'host'" />
      <LedgerPanel v-show="tab === 'ledger'" class="fill" />
      <AdminPanel v-show="tab === 'admin'" />
    </div>
  </div>
</template>
