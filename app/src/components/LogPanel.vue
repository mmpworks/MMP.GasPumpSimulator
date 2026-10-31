<script setup lang="ts">
import { log } from '../sim'
const time = (s: string) => new Date(s).toLocaleTimeString(undefined, { hour12: false }) + '.' + String(new Date(s).getMilliseconds()).padStart(3, '0')
</script>

<template>
  <div>
    <h2>Event log</h2>
    <div class="list">
      <div v-for="(l, i) in [...log].reverse()" :key="i" class="line">
        <span class="muted">{{ time(l.at) }}</span>
        <span class="src" :class="l.source"> {{ l.source }}</span>
        {{ l.message }}
      </div>
    </div>
  </div>
</template>
