<script setup lang="ts">
import { ref } from 'vue'
import { api } from '../sim'

const gallons = ref(600)
const dollarsWanted = ref(2000)
</script>

<template>
  <v-card class="pane-card">
    <v-card-title>Attack the pump</v-card-title>
    <v-card-text>
      <div class="row-wrap mb-2">
        <v-btn color="error" prepend-icon="mdi-key-alert" @click="api.attack('overlimit', { gallons, cents: dollarsWanted * 100 })">Signed token: {{ gallons }} gal, ${{ dollarsWanted }}</v-btn>
        <v-btn color="error" prepend-icon="mdi-pencil-off" @click="api.attack('tamper', { cents: 200000 })">Tamper ceiling to $2000</v-btn>
        <v-btn color="error" prepend-icon="mdi-repeat" @click="api.attack('replay')">Replay last token</v-btn>
        <v-btn color="error" prepend-icon="mdi-key-remove" @click="api.attack('notoken')">Arm with no token</v-btn>
        <v-btn color="error" prepend-icon="mdi-pulse" @click="api.attack('spoof', { pulses: 2000 })">Spoof 2000 pulses</v-btn>
      </div>
      <div class="row-wrap mb-2">
        <v-text-field v-model.number="gallons" type="number" label="gallons" style="max-width: 120px" />
        <v-text-field v-model.number="dollarsWanted" type="number" label="dollars" style="max-width: 120px" />
      </div>
      <p class="muted mb-0">
        The first one is the Detroit case: upstream fully owned, token signed with the real key. The pump clamps it to its own hard limits.
        Watch the ledger for <code>arm.refused</code> and <code>pump.fault</code>.
      </p>
    </v-card-text>
  </v-card>
</template>
