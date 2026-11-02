import { createApp } from 'vue'
import 'vuetify/dist/vuetify.min.css'
import '@mdi/font/css/materialdesignicons.css'
import 'splitpanes/dist/splitpanes.css'
import App from './App.vue'
import { vuetify } from './theme'
import './style.css'

createApp(App).use(vuetify).mount('#app')
