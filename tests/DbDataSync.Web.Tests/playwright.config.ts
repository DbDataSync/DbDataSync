import { defineConfig, devices } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(__dirname, '../..')
// Deliberately outside repoRoot's own git working tree: LibGit2Sharp's repo-validity discovery walks
// up parent directories, so a scratch repo nested inside this project's tree could be seen as
// "already a valid repo" (the parent one) by code that (unlike GitCommitService, see its
// IsRepositoryAt XML doc) uses that discovery-based check.
const scratchRepoRoot = path.join(os.tmpdir(), 'dbdatasync-web-e2e-scratch-repo')

// The one bundled KnownDrivers catalog entry (phase 117) — admin-drivers-libraries.spec.ts installs
// it itself, through the web console's own "Add" button (phase 120), rather than this config file
// seeding it by shelling out to the CLI before the API starts (as an earlier phase-118-only version
// of this suite did): now that installing is a real, tested feature, a test exercising it is a better
// fixture than a shortcut around it.
export const KNOWN_DRIVER_ID = 'mysql.generic'
export const KNOWN_DRIVER_LIBRARY = 'mysql-connector'

// This block is synchronous, top-level code the config file must fully evaluate before Playwright can
// even read `webServer` out of the object below — the only way to guarantee the scratch repo is
// cleared before that process starts, since global-setup.ts runs *concurrently with*, not strictly
// before, webServer (confirmed empirically: the API's own hosted services already touch the repo
// root before global-setup's own code gets a turn to run).
fs.rmSync(scratchRepoRoot, { recursive: true, force: true })
fs.mkdirSync(scratchRepoRoot, { recursive: true })

// Phase 109i: the webServer entry below launches DbDataSync.Api.dll with `dotnet exec` directly, not
// through `dbdatasync serve` — deliberately, per that entry's own comment, so killing it reliably
// kills the real process rather than a `dotnet run` wrapper. That means this scratch repo never goes
// through ServeCommand.EnsureDuckDbInstalledAsync, the one place a real deployment (always started via
// `dbdatasync serve`, confirmed against this repo's own Dockerfile) gets DuckDB installed
// automatically. Seeded here, synchronously, the same way the mysql-connector catalog id used to be
// seeded before phase 120 made installing it a real, UI-testable feature — DuckDB has no equivalent
// "install it" scenario any golden-path test exercises (unlike mysql-connector, nothing here is
// testing the *install*, only relying on DuckDB already being present the way a `dbdatasync serve`
// deployment always would be), so there is no feature test this shortcut would be standing in for.
// A real install (`dbdatasync config library install`), not a hand-rolled fixture — this repo's own
// precedent for anything that has to make a package genuinely loadable, not merely on record.
execFileSync(
  'dotnet',
  ['exec', path.join(repoRoot, 'src/DbDataSync.Cli/bin/Debug/net10.0/DbDataSync.Cli.dll'),
    'config', 'library', 'install', 'duckdb', '--version', '1.5.5', '--repo', scratchRepoRoot],
  { stdio: 'inherit' },
)

// The password used for the real SQL Server test database (started via docker-compose.yml's mssql-source service —
// see architecture/implementation/done/phase-003-mssql-driver.md). This sandbox has no OS keychain, so the
// API's SecretStore falls back to environment variables — presetting these lets the *spawned
// TaskRunner child process* resolve the connection credentials it needs to actually run a
// replication, matching how phase-4/5's manual and integration tests worked around the same gap.
// See global-setup.ts for where these exact connection names get created.
// The suite runs against a deployment that has deliberately turned authentication off — the
// trusted-network mode phase 52 built as its escape hatch. The alternative is Kerberos against a
// Linux container, which is not a thing, and a test-only sign-in backdoor, which would have to be
// impossible to enable in a real deployment and is therefore the wrong thing to add for a test.
const authEnv = {
  DbDataSync__Auth__Network__Admin: 'loopback',
}

// Phase 93: DBDATASYNC_SECRET_* now, not the package's unconfigured "ClrKernel" default — the API's
// SecretStore is constructed with "DbDataSync" as its prefix.
const secretEnv = {
  DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_PLAYWRIGHT_SRC: 'DbDataSync_Test_Pw1',
  DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_PLAYWRIGHT_TGT: 'DbDataSync_Test_Pw1',
}

export default defineConfig({
  testDir: './tests',
  // A CI runner takes noticeably longer to stand up the API, the SPA, a spawned worker and a run
  // against real SQL Server than a dev box does; a step that is comfortably under 60s locally can
  // brush against it there. Double the ceiling on CI so genuine slowness is not read as a hang.
  timeout: process.env.CI ? 120_000 : 60_000,
  fullyParallel: false,
  workers: 1,
  // Locally a failure is a signal to look at; in CI these are end-to-end against real SQL Server,
  // Postgres, a spawned worker and a git-backed config repo on a runner with a fraction of a dev
  // box's headroom, so a first-attempt timeout is often just slowness. A genuinely broken test still
  // fails all three attempts and goes red — a deterministic failure (a missing build output, a real
  // assertion break) is not rescued by a retry.
  retries: process.env.CI ? 2 : 0,
  // Phase 144: on CI, ['list']'s console output is all a failing job's annotations ever carried —
  // nothing named the failing test on the run page itself, which is why nine red runs before this
  // drew no investigation. The 'github' reporter adds those annotations (only meaningful inside a
  // GitHub Actions runner, so gated on CI rather than always on); the small JSON report is uploaded
  // by ci.yml so a failure's test name and error are one click away, no `gh run view --log` required.
  reporter: process.env.CI
    ? [['list'], ['github'], ['json', { outputFile: 'test-results/results.json' }]]
    : [['list']],
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
      // repo path. Run `dotnet build src/DbDataSync.Api` before this suite (see tests/DbDataSync.Web.Tests
      // in architecture/implementation/done/phase-006-spa.md).
      command: 'dotnet exec src/DbDataSync.Api/bin/Debug/net10.0/DbDataSync.Api.dll',
      cwd: repoRoot,
      url: 'http://127.0.0.1:5183/api/health',
      reuseExistingServer: false,
      timeout: 60_000,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ...authEnv,
        ...secretEnv,
        DbDataSync__App__RepoRoot: scratchRepoRoot,
        DbDataSync__State__DbPath: path.join(scratchRepoRoot, 'state.db'),
        ASPNETCORE_URLS: 'http://127.0.0.1:5183',
        ASPNETCORE_ENVIRONMENT: 'Development',
      },
    },
    {
      // `--host 127.0.0.1`, found while verifying phase 103: without it, vite's `--port` alone binds
      // only `[::1]` (IPv6 loopback) on at least one Windows configuration, so the readiness probe
      // above — an IPv4 `127.0.0.1` URL — never connects and this entry times out after 30s even
      // though vite itself started and logged "ready" well within that window. Forcing the bind
      // address to match the probe's own address fixed it outright in manual testing.
      command: 'npm run dev -- --port 5173 --strictPort --host 127.0.0.1',
      cwd: path.join(repoRoot, 'src/DbDataSync.Web'),
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: false,
      timeout: 30_000,
      env: { DBDATASYNC_API_URL: 'http://127.0.0.1:5183' },
    },
  ],
})

export { scratchRepoRoot }
