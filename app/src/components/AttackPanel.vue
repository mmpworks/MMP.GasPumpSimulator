<script setup lang="ts">
import { ref } from 'vue'
import { api } from '../sim'

const gallons = ref(600)
const dollarsWanted = ref(2000)
</script>

<template>
  <div>
    <h2>Attack the pump</h2>
    <div class="grid2">
      <button class="danger" @click="api.attack('overlimit', { gallons, cents: dollarsWanted * 100 })">Signed token: {{ gallons }} gal, ${{ dollarsWanted }}</button>
      <button class="danger" @click="api.attack('tamper', { cents: 200000 })">Tamper ceiling to $2000</button>
      <button class="danger" @click="api.attack('replay')">Replay last token</button>
      <button class="danger" @click="api.attack('notoken')">Arm with no token</button>
      <button class="danger" @click="api.attack('spoof', { pulses: 2000 })">Spoof 2000 pulses</button>
    </div>
    <div class="controls" style="margin-top: 8px">
      <label class="muted">gallons</label><input type="number" v-model.number="gallons" style="width: 80px" />
      <label class="muted">dollars</label><input type="number" v-model.number="dollarsWanted" style="width: 80px" />
    </div>
    <p class="muted" style="margin: 10px 0 0">
      The first one is the Detroit case: upstream fully owned, token signed with the real key. The pump clamps it to its own hard limits.
      Watch the ledger for <code>arm.refused</code> and <code>pump.fault</code>.
    </p>
  </div>
</template>
