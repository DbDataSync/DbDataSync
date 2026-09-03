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
