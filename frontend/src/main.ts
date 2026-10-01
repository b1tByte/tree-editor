import { createApp } from 'vue'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { definePreset } from '@primeuix/themes'
import Aura from '@primeuix/themes/aura'

import 'primeicons/primeicons.css'
import './styles.css'

import App from './App.vue'

const preset = definePreset(Aura, {})

createApp(App)
  .use(createPinia())
  .use(PrimeVue, {
    theme: {
      preset,
      options: {
        // Keep PrimeVue styles in their own cascade layer so Bootstrap and PrimeVue do not fight.
        cssLayer: { name: 'primevue', order: 'bootstrap, primevue' },
        darkModeSelector: false,
      },
    },
  })
  .use(ToastService)
  .use(ConfirmationService)
  .mount('#app')
