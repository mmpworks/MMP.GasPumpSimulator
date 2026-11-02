import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { aliases, mdi } from 'vuetify/iconsets/mdi'
import 'vuetify/dist/vuetify.min.css'
import '@mdi/font/css/materialdesignicons.css'
import 'splitpanes/dist/splitpanes.css'
import App from './App.vue'
import './style.css'

const vuetify = createVuetify({
  components,
  directives,
  icons: { defaultSet: 'mdi', aliases, sets: { mdi } },
  theme: {
    defaultTheme: 'samms',
    themes: {
      samms: {
        dark: true,
        colors: {
          background: '#15100f',
          surface: '#1f1715',
          'surface-variant': '#2a1f1c',
          primary: '#c0392b',
          secondary: '#e8b04a',
          success: '#58c26a',
          warning: '#e8b04a',
          error: '#ff5c4d',
          info: '#e8b04a',
        },
      },
    },
  },
  defaults: {
    VBtn: { density: 'comfortable', variant: 'tonal', size: 'small', class: 'text-none' },
    VTextField: { density: 'compact', variant: 'outlined', hideDetails: true },
    VSelect: { density: 'compact', variant: 'outlined', hideDetails: true },
    VChip: { size: 'small', variant: 'outlined', label: true },
    VCard: { variant: 'flat', rounded: 'lg' },
    VAlert: { density: 'compact', variant: 'tonal' },
  },
})

createApp(App).use(vuetify).mount('#app')
