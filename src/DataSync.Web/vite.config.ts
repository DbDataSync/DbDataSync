import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Proxies API/SignalR calls to DataSync.Api's dev port (see src/DataSync.Api/Properties/launchSettings.json)
// so the SPA can call relative /api and /hubs paths in dev without a CORS setup.
const apiTarget = process.env.DATASYNC_API_URL ?? 'http://localhost:5183'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
})
