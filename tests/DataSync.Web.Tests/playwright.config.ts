import { defineConfig, devices } from '@playwright/test'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../..')
// Deliberately outside repoRoot's own git working tree: LibGit2Sharp's repo-validity discovery walks
// up parent directories, so a scratch repo nested inside this project's tree could be seen as
// "already a valid repo" (the parent one) by code that (unlike GitCommitService, see its
// IsRepositoryAt XML doc) uses that discovery-based check.
const scratchRepoRoot = path.join(os.tmpdir(), 'datasync-web-e2e-scratch-repo')

// The password used for the real SQL Server test database (started via docker-compose.yml's mssql-source service —
// see architecture/implementation/done/phase-3-mssql-driver.md). This sandbox has no OS keychain, so the
// API's SecretStore falls back to environment variables — presetting these lets the *spawned
// TaskRunner child process* resolve the connection credentials it needs to actually run a
// replication, matching how phase-4/5's manual and integration tests worked around the same gap.
// See global-setup.ts for where these exact connection names get created.
const secretEnv = {
  CLRKERNEL_SECRET_DATASYNC_CONNECTION_PLAYWRIGHT_SRC: 'DataSync_Test_Pw1',
  CLRKERNEL_SECRET_DATASYNC_CONNECTION_PLAYWRIGHT_TGT: 'DataSync_Test_Pw1',
}

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
  webServer: [
    {
      // `dotnet exec` on the already-built DLL, not `dotnet run --project` — the latter spawns a
      // wrapper process around the real app process, and killing the wrapper doesn't reliably kill
      // its child, which can leave an orphaned API instance running against a since-deleted scratch
      // repo path. Run `dotnet build src/DataSync.Api` before this suite (see tests/DataSync.Web.Tests
      // in architecture/implementation/done/phase-6-spa.md).
      command: 'dotnet exec src/DataSync.Api/bin/Debug/net10.0/DataSync.Api.dll',
      cwd: repoRoot,
      url: 'http://127.0.0.1:5183/api/health',
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ...secretEnv,
        DataSync__RepoRoot: scratchRepoRoot,
        DataSync__StateDbPath: path.join(scratchRepoRoot, 'state.db'),
        ASPNETCORE_URLS: 'http://127.0.0.1:5183',
        ASPNETCORE_ENVIRONMENT: 'Development',
      },
    },
    {
      command: 'npm run dev -- --port 5173 --strictPort',
      cwd: path.join(repoRoot, 'src/DataSync.Web'),
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: false,
      timeout: 30_000,
      env: { DATASYNC_API_URL: 'http://127.0.0.1:5183' },
    },
  ],
})

export { scratchRepoRoot }
