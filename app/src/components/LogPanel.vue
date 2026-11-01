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
const propsOf = (p: Record<string, string | null> | undefined) => Object.entries(p ?? {}).map(([k, v]) => `${k} = ${v}`).join('\n')
</script>

<template>
  <v-card class="pane-card fixed">
    <v-card-title class="d-flex align-center ga-2 flex-wrap">
      <span>Event log</span>
      <span class="muted" style="letter-spacing: 0; text-transform: none">{{ shown.length }} lines · Herald.OSS</span>
      <v-spacer />
      <v-btn icon="mdi-file-document-outline" size="x-small" variant="text" title="save a plain-text copy since the host started" @click="api.exportLog('text')" />
      <v-btn icon="mdi-code-json" size="x-small" variant="text" title="save a JSON copy with properties since the host started" @click="api.exportLog('json')" />
      <v-btn icon="mdi-broom" size="x-small" variant="text" title="clear the screen; the host keeps its log" @click="clear" />
    </v-card-title>
    <v-card-text>
      <v-text-field v-model="filter" placeholder="filter: sale, pump, payment, attack, admin, text" prepend-inner-icon="mdi-magnify" clearable class="mb-2 flex-grow-0" />
      <div v-if="logFile" class="muted mb-2" :title="'Herald.OSS NDJSON file, with properties: ' + logFile.path">
        hover a line for its properties · file: <code>{{ logFile.path.split(/[\\/]/).pop() }}</code>
      </div>
      <div class="mono-list">
        <div v-for="(l, i) in shown" :key="shown.length - i" class="line" :class="'sev-' + l.severity" :title="propsOf(l.props)">
          <span class="muted">{{ time(l.at) }}</span>
          <span class="src" :class="l.source">{{ l.source }}</span>
          <span>{{ l.message }}</span>
        </div>
      </div>
    </v-card-text>
  </v-card>
</template>
