<script setup lang="ts">
import { ref, computed } from 'vue'
import { api, log, logFile } from '../sim'

const time = (s: string) => new Date(s).toLocaleTimeString(undefined, { hour12: false }) + '.' + String(new Date(s).getMilliseconds()).padStart(3, '0')
const filter = ref('')
const shown = computed(() => {
  const f = filter.value.trim().toLowerCase()
  const rows = f ? log.filter(l => l.source.includes(f) || l.message.toLowerCase().includes(f)) : log
  return [...rows].reverse()
})
// Clears the screen only. The host keeps its full log; a reconnect brings the last 200 lines back.
const clear = () => log.splice(0)
</script>

<template>
  <div>
    <div class="controls" style="margin-bottom: 8px">
      <h2 style="margin: 0; flex: 1">Event log <span class="muted">({{ shown.length }}, Herald.OSS, hover a line for its properties)</span></h2>
      <input v-model="filter" placeholder="filter: sale, pump, payment, attack, text" style="width: 260px" />
      <button @click="api.exportLog('text')" title="plain text, since the host started">Save .txt</button>
      <button @click="api.exportLog('json')" title="JSON with properties, since the host started">Save .json</button>
      <button @click="clear">Clear screen</button>
    </div>
    <div class="muted" style="margin-bottom: 6px" v-if="logFile" :title="logFile.format">
      Herald.OSS is also writing every line, with its properties, to <code>{{ logFile.path }}</code>
    </div>
    <div class="list">
      <div v-for="(l, i) in shown" :key="shown.length - i" class="line cols" :class="'sev-' + l.severity" :title="Object.entries(l.props ?? {}).map(([k, v]) => k + ' = ' + v).join('\n')">
        <span class="muted">{{ time(l.at) }}</span>
        <span class="src" :class="l.source">{{ l.source }}</span>
        <span>{{ l.message }}</span>
      </div>
    </div>
  </div>
</template>
