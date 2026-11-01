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
    defaultTheme: 'pump',
    themes: {
      pump: {
        dark: true,
        colors: {
          background: '#0f1115',
          surface: '#171a21',
          'surface-variant': '#202633',
          primary: '#1f6feb',
          secondary: '#58a6ff',
          success: '#3fb950',
          warning: '#d29922',
          error: '#f85149',
          info: '#58a6ff',
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
