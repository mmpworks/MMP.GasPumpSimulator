import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { aliases, mdi } from 'vuetify/iconsets/mdi'

// One Vuetify theme per station. The host names its theme in the state push; App.vue switches to it.
// The CSS variables for the same names live in style.css under :root[data-theme="..."].
export const vuetify = createVuetify({
  components,
  directives,
  icons: { defaultSet: 'mdi', aliases, sets: { mdi } },
  theme: {
    defaultTheme: 'howards',
    themes: {
      howards: {
        dark: true,
        colors: {
          background: '#0e1412',
          surface: '#151d1a',
          'surface-variant': '#1d2924',
          primary: '#1f8a6b',
          secondary: '#43c59e',
          success: '#3fb950',
          warning: '#d29922',
          error: '#f85149',
          info: '#43c59e',
        },
      },
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

/** Apply a theme the host named. Unknown names keep the current theme, so a typo in GASPUMP_THEME is harmless. */
export function applyTheme(name: string) {
  document.documentElement.dataset.theme = name
  if (name in vuetify.theme.themes.value) vuetify.theme.global.name.value = name
}
