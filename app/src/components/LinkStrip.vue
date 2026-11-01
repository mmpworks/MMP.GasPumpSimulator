<script setup lang="ts">
import { computed } from 'vue'
import { state } from '../sim'

// The wire. Frames down are PumpMessages the station sent; frames up are events, views, log, ledger,
// and custody lines the dispenser sent. Both counters come from the station's end of the link.
const d = computed(() => state.value?.dispenser)
const up = computed(() => !!d.value?.connected)
const kind = computed(() => d.value?.link ?? 'socket')
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
    <div class="wire" :class="{ live: up }"></div>
    <div class="link-foot">
      <template v-if="kind === 'socket'">
        <div>JSON lines</div>
        <div>one per frame</div>
        <div class="mt-2">{{ up ? 'both processes up' : 'dispenser unreachable' }}</div>
      </template>
      <template v-else>
        <div>same process</div>
        <div>bounded mailbox</div>
      </template>
    </div>
  </div>
</template>
