# Run the Playwright E2E suite in CI

**Status: resolved 2026-09-03 — add it, don't add a component-test toolchain.**

## Where this came from

`spa-has-no-component-test-layer.md` raised two separable gaps: no component-test toolchain for
pure-function/prop-level SPA tests, and the Playwright suite never running anywhere but a developer's
machine. Resolved to build the second, not the first — the E2E suite already exists, already works, and
not running it in CI is what let a real regression (`apply-button-does-not-cache-provisioned-columns.md`'s
test 18) sit failing on `main` since phase 94 with nobody noticing. A component-test toolchain is a
second toolchain to keep working indefinitely; running a suite that already exists is not.

## What's there today, confirmed by reading the code

- `.github/workflows/ci.yml` has four jobs: `dotnet`, `dotnet-integration` (spins up `mssql-source`,
  `mssql-target`, `postgres` as service containers — the same topology `docker-compose.yml` uses
  locally), `web` (just `npm run build`), and `package`. None runs Playwright.
- `tests/DbDataSync.Web.Tests/playwright.config.ts` already automates everything a CI job would
  otherwise have to orchestrate by hand: its `webServer` array starts both the API
  (`dotnet exec .../DbDataSync.Api.dll`, pre-built, auth disabled, a scratch git repo root) and the SPA
  dev server (`npm run dev`), waits on each's health check, and tears them down after. `globalSetup`/
  `globalTeardown` handle whatever fixture-level setup the suite needs beyond that.
- The suite is written against real source/target SQL Server containers on the same ports
  `docker-compose.yml` and `dotnet-integration`'s service containers already use (14330/14331), plus
  Postgres on 15432 — the exact three containers `dotnet-integration` already stands up.

## Design

A new `playwright` job in `ci.yml`, modeled directly on `dotnet-integration`'s service-container block
(same three containers, same ports, same images) plus `web`'s Node setup:

1. Check out, set up .NET and Node.
2. `dotnet build` (the API needs to be pre-built — `playwright.config.ts`'s `webServer` runs the DLL
   directly via `dotnet exec`, not `dotnet run`).
3. `npm ci` in `src/DbDataSync.Web`, then `npx playwright install --with-deps chromium` (the browser
   binary isn't part of `npm ci` and CI runners don't have one pre-installed).
4. `npx playwright test` from `tests/DbDataSync.Web.Tests` — the existing config handles starting both
   app processes.

## What this phase should not do

- Add vitest/Testing Library or any component-test toolchain — explicitly deferred, per the resolution
  above.
- Change anything about how the suite itself runs locally, or its `webServer`/fixture setup — CI should
  reuse it exactly as written, not fork a CI-specific variant.
- Attempt to parallelize or shard the suite — it already runs `workers: 1`/`fullyParallel: false`
  deliberately (per its own config); revisit only if CI runtime becomes a real problem.

## How to verify

- A CI run (or a local simulation of the same steps) confirms the suite passes in the new job.
- Confirm the new job actually would have caught `apply-button-does-not-cache-provisioned-columns.md`'s
  test 18 — i.e., don't accidentally scope the new job to skip that spec or any other.
- The existing three jobs (`dotnet`, `dotnet-integration`, `web`) remain untouched and unaffected.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-098-playwright-suite-in-ci.md`.
