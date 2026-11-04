<script setup lang="ts">
import { computed, ref } from 'vue'

// One file on disk: its full path as selectable text, with buttons that copy the path or its folder for Explorer.
const props = defineProps<{ label: string; path: string | null | undefined }>()

const folder = computed(() => {
  const p = props.path ?? ''
  const cut = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'))
  return cut > 0 ? p.slice(0, cut) : p
})
const copied = ref<string | null>(null)
let clear: ReturnType<typeof setTimeout> | undefined
async function copy(text: string, what: string) {
  await navigator.clipboard.writeText(text)
  copied.value = what
  clearTimeout(clear)
  clear = setTimeout(() => (copied.value = null), 1500)
}
</script>

<template>
  <div class="file-path">
    <span class="muted file-path-label">{{ label }}</span>
    <code v-if="path">{{ path }}</code>
    <span v-else class="muted">not available</span>
    <template v-if="path">
      <v-btn size="x-small" variant="text" icon="mdi-content-copy" aria-label="Copy path" title="Copy the full path" @click="copy(path!, 'path')" />
      <v-btn size="x-small" variant="text" icon="mdi-folder-outline" aria-label="Copy folder" title="Copy the folder, to paste into Explorer" @click="copy(folder, 'folder')" />
    </template>
    <span v-if="copied" class="muted">{{ copied }} copied</span>
  </div>
</template>
