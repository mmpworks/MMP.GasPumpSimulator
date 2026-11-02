<script setup lang="ts">
import { computed } from 'vue'
import { state, pumpState } from '../sim'

// The link between the two processes. Frames down are PumpMessages the station sent. Frames up are
// events, views, log, ledger, and custody lines the dispenser sent. Both counters come from the
// station's end of the link.
const d = computed(() => pumpState.value?.dispenser)
const up = computed(() => !!d.value?.connected)
const kind = computed(() => d.value?.link ?? 'socket')
// Heartbeat age in whole seconds, from the station's clock in the same state push. Null before the first beat.
const beatAge = computed(() => {
  const at = d.value?.heartbeatAt; const now = state.value?.now
  if (!at || !now) return null
  return Math.max(0, Math.round((new Date(now).getTime() - new Date(at).getTime()) / 1000))
})
const silent = computed(() => up.value && d.value?.peerAlive === false)
const openTx = computed(() => pumpState.value?.pump.openTransactions?.length ?? 0)
</script>

<template>
  <div class="link-strip" :class="{ up, down: !up }">
    <div class="link-label">LINK</div>
    <v-icon size="28" :color="up ? 'success' : 'error'">{{ up ? 'mdi-lan-connect' : 'mdi-lan-disconnect' }}</v-icon>
    <div class="link-kind">{{ kind === 'socket' ? 'TCP' : 'mailbox' }}</div>
    <div v-if="kind === 'socket'" class="link-port">127.0.0.1<br />:{{ d?.port ?? '-' }}</div>
    <div class="wire" :class="{ live: up }"></div>
    <div class="counter" title="PumpMessages the station posted to the dispenser">
      <v-icon size="16">mdi-arrow-left-bold</v-icon>
      <b>{{ d?.framesSent ?? 0 }}</b>
      <span>msgs</span>
    </div>
    <div class="counter" title="frames the dispenser sent back: events, views, log, ledger, custody">
      <v-icon size="16">mdi-arrow-right-bold</v-icon>
      <b>{{ d?.framesReceived ?? 0 }}</b>
      <span>frames</span>
    </div>
    <div class="counter" title="custody chain lines received byte for byte">
      <v-icon size="16">mdi-link-lock</v-icon>
      <b>{{ d?.custodyLines ?? 0 }}</b>
      <span>chain</span>
    </div>
    <div v-if="kind === 'socket'" class="counter" :title="`sequence numbers: ${d?.lastSeqSent ?? 0} sent, ${d?.lastSeqReceived ?? 0} received; ${d?.gaps ?? 0} gaps asked for, ${d?.duplicates ?? 0} duplicates dropped`" data-testid="link-seq">
      <v-icon size="16">mdi-counter</v-icon>
      <b>{{ d?.lastSeqReceived ?? 0 }}</b>
      <span>seq{{ (d?.gaps ?? 0) > 0 ? ` · ${d?.gaps} gap` : '' }}</span>
    </div>
    <div v-if="kind === 'socket'" class="counter" :class="{ silent }" :title="silent ? 'the socket is open and the dispenser stopped talking' : `last heartbeat from the dispenser, status ${d?.peerStatus ?? '-'}`" data-testid="link-heartbeat">
      <v-icon size="16" :color="silent ? 'error' : undefined">{{ silent ? 'mdi-heart-broken' : 'mdi-heart-pulse' }}</v-icon>
      <b>{{ beatAge === null ? '-' : `${beatAge}s` }}</b>
      <span>{{ silent ? 'SILENT' : 'beat' }}</span>
    </div>
    <div v-if="openTx > 0" class="counter" title="deliveries the island still holds until the station settles them" data-testid="link-open-tx">
      <v-icon size="16" color="warning">mdi-receipt-text-clock</v-icon>
      <b>{{ openTx }}</b>
      <span>payable</span>
    </div>
    <div class="wire" :class="{ live: up }"></div>
    <div class="link-foot">
      <template v-if="kind === 'socket'">
        <div>JSON lines</div>
        <div>one per frame</div>
        <div class="mt-2">{{ silent ? 'dispenser silent' : up ? 'both processes up' : 'dispenser unreachable' }}</div>
      </template>
      <template v-else>
        <div>same process</div>
        <div>bounded mailbox</div>
      </template>
    </div>
  </div>
</template>
