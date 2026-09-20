import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Proxies API/SignalR calls to DbDataSync.Api's dev port (see src/DbDataSync.Api/Properties/launchSettings.json)
// so the SPA can call relative /api and /hubs paths in dev without a CORS setup.
const apiTarget = process.env.DBDATASYNC_API_URL ?? 'http://localhost:5183'

export default defineConfig({
  plugins: [react()],
  // Renderer tests run in plain Node: components are rendered to a string with react-dom/server, which is enough to
  // assert what markup a document produces (and, for the security cases, what markup it must not) without a DOM.
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}'] },
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
})
