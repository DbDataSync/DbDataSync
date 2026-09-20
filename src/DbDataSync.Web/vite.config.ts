import react from '@vitejs/plugin-react'
import fs from 'node:fs'
import path from 'node:path'
import type { Plugin } from 'vite'
import { defineConfig } from 'vitest/config'

// Proxies API/SignalR calls to DbDataSync.Api's dev port (see src/DbDataSync.Api/Properties/launchSettings.json)
// so the SPA can call relative /api and /hubs paths in dev without a CORS setup.
const apiTarget = process.env.DBDATASYNC_API_URL ?? 'http://localhost:5183'

// In a published build the docs are files in wwwroot/docs, put there by the build (phase 160). Vite's dev server has
// no such folder, so serve the repository's own docs/ at the same address — the viewer then works unchanged in dev,
// and edits to a page show up on the next load.
const docsDir = path.resolve(import.meta.dirname, '../../docs')
const serveRepoDocs: Plugin = {
  name: 'serve-repo-docs',
  configureServer(server) {
    server.middlewares.use((req, res, next) => {
      const match = /^\/docs\/([a-z0-9-]+)\.md$/i.exec((req.url ?? '').split('?')[0])
      const file = match && path.join(docsDir, `${match[1]}.md`)
      if (!file || !fs.existsSync(file)) return next()
      res.setHeader('Content-Type', 'text/markdown; charset=utf-8')
      res.end(fs.readFileSync(file))
    })
  },
}

export default defineConfig({
  plugins: [react(), serveRepoDocs],
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
