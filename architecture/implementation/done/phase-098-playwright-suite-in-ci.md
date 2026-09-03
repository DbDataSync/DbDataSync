# Phase 98 — Run the Playwright suite in CI

**Status**: Not started.
**Plan reference**: `architecture/planning/done/playwright-suite-in-ci.md`

## The gap

`.github/workflows/ci.yml` never runs the Playwright E2E suite — only `npm run build`. This is exactly
why `apply-button-does-not-cache-provisioned-columns.md`'s test 18 sat failing on `main` since phase 94
with nobody noticing.

## What to build

A new `playwright` job in `ci.yml`, modeled on `dotnet-integration`'s existing service-container block:

1. `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-node@v4` (matching `web`'s Node
   setup).
2. Service containers: the same `mssql-source` (14330), `mssql-target` (14331), `postgres` (15432)
   `dotnet-integration` already defines — copy that block rather than re-deriving it.
3. `dotnet build` — the API must be pre-built, since `playwright.config.ts`'s `webServer` runs
   `dotnet exec .../DbDataSync.Api.dll` directly, not `dotnet run`.
4. `npm ci` in `src/DbDataSync.Web`.
5. `npx playwright install --with-deps chromium` in `tests/DbDataSync.Web.Tests` — the browser binary,
   not part of `npm ci`.
6. `npx playwright test` from `tests/DbDataSync.Web.Tests` — the existing `webServer`/`globalSetup`
   config handles starting both app processes; nothing new to orchestrate.

## What this phase should not do

- Add a component-test toolchain (vitest/Testing Library) — explicitly deferred.
- Modify `playwright.config.ts`, `global-setup.ts`, or any spec file to make them "CI-friendly" — reuse
  exactly what already runs locally. If something genuinely doesn't work in CI, fix the actual cause
  rather than fork a CI-specific config.
- Parallelize or shard the suite — it's deliberately `workers: 1`/`fullyParallel: false` today.
- Touch the existing `dotnet`, `dotnet-integration`, `web`, or `package` jobs.

## How to verify

- The new job passes in a real CI run (or as close a local simulation as practical — spin up the same
  three containers, build, and run the suite exactly as the job would).
- Confirm the new job's scope actually includes `apply-button-does-not-cache-provisioned-columns.md`'s
  regression test (`golden-path.spec.ts` test 18) — don't accidentally filter it out.
- The other four jobs are untouched and their behavior is unaffected.

---

# Outcome

A `playwright` job in `.github/workflows/ci.yml`, between `web` and `package`. The diff is 58 added
lines and **zero deleted** — `dotnet`, `dotnet-integration`, `web` and `package` are byte-for-byte
untouched, which is the cheapest way to guarantee the "other four jobs unaffected" requirement.

Nothing under `tests/DbDataSync.Web.Tests/` was modified. The suite runs in CI exactly as it runs on a
developer's machine, which is what both documents asked for.

### The deviation: docker-compose.yml, not `services:`

Both documents said to copy `dotnet-integration`'s service-container block verbatim. That block would
not have worked, and the reason is worth recording because it is invisible until you read the suite's
own helper rather than its config.

`tests/DbDataSync.Web.Tests/test-db.ts:32` reaches SQL Server by **container name**, not through a
published port:

```ts
const args = ['exec', 'dbdatasync-mssql-source', ...sqlcmdArgs(), '-S', 'localhost', '-U', 'sa', ...]
execFileSync('docker', args, { stdio: 'inherit' })
```

`globalSetup` and `globalTeardown` both go through that `runSql`, as does `querySql` in the assertions
of `golden-path.spec.ts`. GitHub Actions names service containers itself, so `docker exec
dbdatasync-mssql-source` would have failed on the very first line of `globalSetup` — before a single
test ran, and with an error ("No such container") that points nowhere near the cause.

`docker-compose.yml` is what makes that name exist, via `container_name:` on all three services. So the
job runs `docker compose up -d --wait mssql-source mssql-target postgres` instead. This is *less*
duplication than the doc's plan, not more: the ports, images, passwords, `MSSQL_AGENT_ENABLED` and
health checks stop being a second copy that can drift from the file the suite is actually written
against. `--wait` blocks on the healthchecks compose already declares, so it gates on readiness the
same way `services:` would have.

The alternative — keeping `services:` and passing `--name dbdatasync-mssql-source` through `options:`
— relies on undocumented flag-ordering behaviour in how the runner composes its `docker create`, and
could not have been verified anywhere but a real CI run. Rejected on that basis.

### The other two calls the doc left open

**Health-check tuning: none.** Compose's own `start_period: 30s` plus 10 retries is what every
developer already runs against, and `--wait` honours it. Nothing here is more timing-sensitive than
`dotnet-integration`, which uses the same containers with no tuning.

**Browser install is inline, not cached.** Checked for an existing Playwright-cache pattern first;
there is none anywhere in the repo. `npx playwright install --with-deps chromium` is a step of its own
because `--with-deps` needs the runner's passwordless `sudo` (it fails locally for exactly that
reason), but it is not wrapped in `actions/cache`. Caching a browser binary keyed on a Playwright
version is a small amount of machinery to save ~40s on a job that takes ~8 minutes, and a stale
browser cache is a confusing failure. Revisit if the job's runtime becomes a real problem.

### Two additions the doc did not ask for

**A second `npm ci`.** The doc lists `npm ci` only in `src/DbDataSync.Web`. But
`tests/DbDataSync.Web.Tests` has its own `package.json` and its own `package-lock.json`, and
`@playwright/test` is *only* there. Without it, `npx playwright test` would fetch some arbitrary latest
Playwright at run time rather than the pinned `^1.48.0`. Both lockfiles are listed in setup-node's
`cache-dependency-path`.

**Trace upload on failure.** `playwright.config.ts` already sets `trace: 'retain-on-failure'`, and
without an `upload-artifact` step that trace dies with the runner — in the one situation it exists for,
a failure that only reproduces in CI. `if: failure()`, 7-day retention.

### How it was verified

Locally, running the CI steps in order against the real containers. Not a paraphrase of them — the same
commands, in the same directories.

- `docker compose up -d --wait mssql-source mssql-target postgres` — exit 0, all three reported
  `Healthy`.
- `dotnet build src/DbDataSync.Api` — **0 warnings, 0 errors**. Debug deliberately: `webServer` runs
  `bin/Debug/net10.0/DbDataSync.Api.dll` through `dotnet exec`, so `-c Release` would leave that path
  empty and the API would never start. This is the single easiest thing to get wrong in this job.
- `npm ci` in both `src/DbDataSync.Web` and `tests/DbDataSync.Web.Tests` — exit 0.
- `npx playwright install --with-deps chromium` — fails locally on `sudo: a password is required`,
  which is a property of this sandbox, not the job; GitHub runners have passwordless sudo. Verified the
  binary-only `npx playwright install chromium` succeeds and that the suite then runs.
- `npx playwright test` from `tests/DbDataSync.Web.Tests` — the config's `webServer` array started and
  stopped both the API and the Vite dev server, and `globalSetup`/`globalTeardown` built and dropped
  `DbDataSyncPlaywrightTest`, with nothing else orchestrating them. As designed.
- YAML parsed with `yaml.safe_load` and the job's step list read back to confirm every
  `working-directory` and `run` landed where intended.

**The job does what the phase was created for.** On `main` as it stood, the suite ran **45 passed, 1
failed, 25 did not run** — and the one failure was exactly
`apply-button-does-not-cache-provisioned-columns.md`'s test 18, with the identical cached-target-metadata
error the planning doc records. So the answer to "would this job have caught the regression that
motivated it" is not a judgement call: it was reproduced by running it. The 25 skips are
`test.describe.serial` at `golden-path.spec.ts:49` abandoning the chain after a failure, not a scoping
mistake — nothing filters any spec out.

Phase 97's fix for that regression landed in the working tree while this phase was being built.
Rebuilding the API against it and running the full suite gives **71 passed, 0 failed, exit code 0** in
3.1 minutes — every spec file, every test. That is the state the new job will report green on.

### What CI will surface that nobody has seen yet

Recorded because it is the predictable first complaint about this job, and it is not a defect in the
job.

Nothing in this suite has run past `golden-path.spec.ts` test 18 since phase 94 — the serial abort saw
to that. Tests 19-45 have therefore been unobserved for four phases, and they are not perfectly
deterministic. Across runs here, test 41 (`a segmenting strategy is authored, tested and used`) failed
twice and passed once, and test 43 (`the natural key is auto-derived`) failed once. Test 41 is not slow
— when it passes it takes **8.2 seconds** against a 60s budget — so this is order- or state-dependence
between tests, not a timeout that wants raising.

A clean full-suite pass is achievable and was observed. But this job will occasionally go red on that
tail, and the right response is to stabilise those tests rather than to retry the job or to reach for
`retries: 1` in a config that deliberately sets `retries: 0`.
