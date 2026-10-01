import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// In development the UI proxies /api to the .NET API, so no CORS setup is needed.
const apiTarget = process.env.API_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
    },
  },
  test: {
    environment: 'node',
  },
})
