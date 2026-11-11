<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted } from 'vue'
import { HOST, activePump, pumpState } from '../sim'

// The secure card reader on the open pump (GasPump.Reader, on MMP.NetworkSecurity's AES DUKPT).
// Three views of one card sale: the reader (its key serial number and counter, never a key), the terminal
// (what crossed it: ciphertext, a PIN block, masked digits), and the bank (what it opened: a length, the
// last 4, a PIN result). Training keys only: see docs/threat-model.md.
interface ReaderStatus { initialKeyId: string; lastKsn: string | null; lastCounter: number | null; nextCounter: number; heldKeyRegisters: number }
interface ProtectedCard { ksn: string; sealedPan: string; pinBlock: string | null; maskedPan: string; kind: string; expiryMonth: number; expiryYear: number; zip: string | null; readerCheck: string }
interface BankView { ksn: string; opened: boolean; refusal: string; panDigits: number; last4: string; pinChecked: boolean; decision: string }
interface Reader { status: ReaderStatus; terminal: ProtectedCard | null; bank: BankView | null; attacks: string[] }
interface AttackResult { attack: string; defended: boolean; code: string; message: string }

const reader = ref<Reader | null>(null)
const result = ref<AttackResult | null>(null)
const labels: Record<string, { text: string; icon: string; hint: string }> = {
  'replay': { text: 'Replay last card', icon: 'mdi-repeat', hint: 'send the recorded message again, with its KSN' },
  'tamper-pin': { text: 'Change a PIN block byte', icon: 'mdi-dialpad', hint: 'debit only: flip one bit of the PIN block' },
  'tamper-card': { text: 'Change a card number byte', icon: 'mdi-credit-card-edit', hint: 'flip one bit of the sealed card number' },
  'rogue-reader': { text: 'Reader with another base key', icon: 'mdi-credit-card-scan', hint: 'a reader this bank never injected' },
  'read-memory': { text: 'Read the reader memory', icon: 'mdi-memory', hint: 'try to rebuild the last sale key from the keys left in the reader' },
}

async function refresh() {
  const r = await fetch(`${HOST}/api/pump/${activePump.value}/reader`)
  if (r.ok) reader.value = (await r.json()) as Reader
}
async function run(kind: string) {
  const r = await fetch(`${HOST}/api/pump/${activePump.value}/reader/attack/${kind}`, { method: 'POST' })
  result.value = (await r.json()) as AttackResult
  await refresh()
}
const short = (hex: string | null | undefined, n = 16) => (hex ? (hex.length > n * 2 ? `${hex.slice(0, n)}…${hex.slice(-n)}` : hex) : '-')

let timer: number | undefined
onMounted(() => { refresh(); timer = window.setInterval(refresh, 1000) })
onUnmounted(() => window.clearInterval(timer))
watch(activePump, () => { result.value = null; refresh() })
watch(() => pumpState.value?.sale?.state, refresh)
</script>

<template>
  <v-card class="pane-card" data-testid="reader-panel">
    <v-card-title>Card reader · AES DUKPT</v-card-title>
    <v-card-text>
      <div class="step">Reader</div>
      <div class="kv mb-2">
        <b>initial key id</b><span class="mono" data-testid="reader-ikid">{{ reader?.status.initialKeyId ?? '-' }}</span>
        <b>last KSN</b><span class="mono" data-testid="reader-ksn">{{ reader?.status.lastKsn ?? 'no card read yet' }}</span>
        <b>counter</b><span data-testid="reader-counter">{{ reader?.status.lastCounter ?? '-' }} used · next {{ reader?.status.nextCounter ?? '-' }} · {{ reader?.status.heldKeyRegisters ?? '-' }} key registers held</span>
      </div>

      <div class="step">What the terminal sees</div>
      <div class="kv mb-2">
        <b>card</b><span class="mono" data-testid="terminal-masked">{{ reader?.terminal?.maskedPan ?? '-' }} {{ reader?.terminal ? `· ${reader.terminal.kind} · exp ${String(reader.terminal.expiryMonth).padStart(2, '0')}/${reader.terminal.expiryYear}` : '' }}</span>
        <b>sealed number</b><span class="mono" data-testid="terminal-sealed">{{ short(reader?.terminal?.sealedPan) }}</span>
        <b>PIN block</b><span class="mono" data-testid="terminal-pinblock">{{ reader?.terminal?.pinBlock ?? (reader?.terminal ? 'none (credit)' : '-') }}</span>
        <b>ZIP</b><span>{{ reader?.terminal?.zip ?? '-' }}</span>
      </div>

      <div class="step">What the bank opens</div>
      <div class="kv mb-2">
        <b>card data</b>
        <span data-testid="bank-opened" :class="reader?.bank && !reader.bank.opened ? 'text-error' : undefined">
          <template v-if="!reader?.bank">nothing yet</template>
          <template v-else-if="reader.bank.opened">opened: {{ reader.bank.panDigits }} digits, ending {{ reader.bank.last4 }}{{ reader.bank.pinChecked ? ' · PIN block opened and checked' : '' }}</template>
          <template v-else>refused: {{ reader.bank.refusal }}</template>
        </span>
        <b>decision</b><span data-testid="bank-decision">{{ reader?.bank?.decision ?? '-' }}</span>
      </div>

      <div class="step">Attack the card path</div>
      <div class="row-wrap mb-2">
        <v-btn v-for="a in reader?.attacks ?? []" :key="a" color="error" size="small" :prepend-icon="labels[a]?.icon" :title="labels[a]?.hint"
               :data-testid="`reader-attack-${a}`" @click="run(a)">{{ labels[a]?.text ?? a }}</v-btn>
      </div>
      <v-alert v-if="result" :type="result.defended ? 'success' : (result.code === 'NotRun' ? 'info' : 'error')" density="compact" class="mb-2" data-testid="reader-attack-result">
        <b>{{ result.code }}</b> · {{ result.message }}
      </v-alert>
      <p class="muted mb-0">
        Training keys: the base key is the published ANSI X9.24-3 test key, so anyone can check every value. The reader is a
        process boundary in a simulator, with no tamper hardware, and makes no PCI claim.
      </p>
    </v-card-text>
  </v-card>
</template>
