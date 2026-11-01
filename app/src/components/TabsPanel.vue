<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { state } from '../sim'
import NetworkPanel from './NetworkPanel.vue'
import LedgerPanel from './LedgerPanel.vue'
import AdminPanel from './AdminPanel.vue'

type Tab = 'host' | 'ledger' | 'admin'
const tab = ref<Tab>('host')
try { const saved = localStorage.getItem('gaspump.tab') as Tab | null; if (saved) tab.value = saved } catch { /* private window */ }
watch(tab, t => { try { localStorage.setItem('gaspump.tab', t) } catch { /* ignore */ } })

// A live hint on each tab so a change elsewhere is visible without switching.
const hostHint = computed(() => state.value?.network.mode ?? '')
const ledgerBroken = computed(() => state.value ? !state.value.ledgerOk : false)
const adminLocked = computed(() => !!state.value?.site.lockedUntil)
</script>

<template>
  <v-card class="pane-card fixed">
    <v-tabs v-model="tab" density="compact" color="primary" grow>
      <v-tab value="host" prepend-icon="mdi-lan-connect" class="text-none">Host link <v-chip class="ml-2" size="x-small">{{ hostHint }}</v-chip></v-tab>
      <v-tab value="ledger" prepend-icon="mdi-link-variant" class="text-none">Ledger <v-chip v-if="ledgerBroken" class="ml-2" size="x-small" color="error" variant="flat">BROKEN</v-chip></v-tab>
      <v-tab value="admin" prepend-icon="mdi-shield-account" class="text-none">Administrator <v-chip v-if="adminLocked" class="ml-2" size="x-small" color="error" variant="flat">LOCKED</v-chip></v-tab>
    </v-tabs>
    <v-divider />
    <v-card-text>
      <!-- v-show keeps each tab's state (typed PIN, scroll position) while hidden. -->
      <NetworkPanel v-show="tab === 'host'" />
      <LedgerPanel v-show="tab === 'ledger'" />
      <AdminPanel v-show="tab === 'admin'" />
    </v-card-text>
  </v-card>
</template>
