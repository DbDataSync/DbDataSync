# Phase 120 — web-triggered library install / remove; SDK becomes the default image

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The web endpoints and screens*, §*Trust, build environment, and restart*, Decision 1. Depended on
phases 116–119. Last phase of the committed arc; phases 121–122 are follow-on.

## What this built

### The default Docker image moves to an SDK base

`Dockerfile`'s final stage: `mcr.microsoft.com/dotnet/aspnet:10.0` → `mcr.microsoft.com/dotnet/sdk:10.0`
(the `build` stage was already on it). Confirmed by building the image and running
`config library install Microsoft.Data.Sqlite --version 9.0.0` inside a live container — a real
`dotnet publish` restore against the public feed, from inside the running image, no container changes
needed beyond the base image swap. Measured size: **1.36GB** (vs. `aspnet:10.0`'s 230MB) — bigger than
the plan doc's own ~750MB estimate, noted here rather than corrected there (the estimate was a guess
before anyone had actually built and measured it). No CI step asserts an image size, so nothing else in
`.github/workflows/*.yml` needed touching; the existing package job's build-and-health-check smoke test
covers the SDK image exactly as it did the old one.

### API

- **`POST /api/libraries`** ← `{ packageId, version, factoryType?, source? }` on `LibrariesController`.
  Runs `LibraryInstaller.InstallAsync` synchronously — the SDK is present by construction, so no probe,
  no "pending" state. `factoryType` required only when `packageId` isn't in `KnownLibraries`. A failed
  `dotnet publish` returns `400` with the tail of its stderr (`InstallErrorFormatting.TailOf`, 20 lines),
  not a bare `500`.
- **`POST /api/drivers/from-catalog`** ← `{ knownDriverId, version }` on `DriversController`. Installs
  the catalog entry's bound library (reusing it if already installed, exactly like `config driver
  install`'s CLI behaviour), writes the bundled descriptor, and — new, beyond what the CLI does —
  **registers the driver into the live `DriverRegistry` immediately**, the same read-descriptor
  /resolve-factory/register-`GenericDriver` step `DriverLoader.LoadDescriptorDrivers` does at startup,
  just for this one entry. A failure there is logged and skipped (the same `onError` contract), not
  fatal to the request — the descriptor is still on disk and loads for real on the next restart either
  way.
- **`DELETE /api/libraries/{id}`** — `409` (with the using-driver list) when `usedBy` is non-empty,
  `?force=true` overrides. In-use is in-use regardless of `resolves`.
- **`LibraryRegistry` gained `RegisterInstalled(id)` and `Remove(id)`** so `POST`/`DELETE` update the
  same live singleton `GET /api/libraries` reads, without a restart — see Decisions for why this
  mattered and why it isn't a full rescan.
- **A real restart-required signal**: new `RestartRequiredState` (a marker file in the repo root,
  touched by any of these mutations plus `AdminConfigService.Set`/`SetStateConnectionSecret`, cleared
  once at the next real startup) and `GET /api/admin/restart-required`, replacing the Configuration
  screen's previously client-only flag — resolving this phase's own open question in favor of "a
  lightweight server flag both screens read."
- All mutating actions `[Authorize(Policies.Admin)]`.

### SPA

- `AdminLibrariesPage.tsx`: the search/quick-add/manual-entry flow from 119 now ends in a real
  **Install** button (curated pick installs directly; anything else opens a trust confirmation dialog
  first) instead of only a copyable command — the command stays, as a fallback. A **Remove** button per
  row, disabled with a tooltip while `usedBy` is non-empty, plus a **Force** link (behind a native
  confirm naming the affected drivers) that still works while disabled.
- `AdminDriversPage.tsx`: each catalog entry gets a version field and a real **Add** button (no trust
  dialog — a catalog pick is curated by definition); already-installed entries are filtered out of the
  list.
- `AdminConfigPage.tsx`'s restart banner now also reads the shared server flag (`useRestartRequired`),
  alongside its existing same-session local flag — the server flag is what a different admin's tab, or
  this one after a reload, actually sees; `AdminCertificatePage.tsx` was left on its own local-only flag
  (see Decisions).
- `api/client.ts` + `api/hooks.ts`: `useInstallLibrary`, `useRemoveLibrary`,
  `useInstallDriverFromCatalog`, `useRestartRequired`; `useSetAdminConfig`/`useSetAdminConfigSecret` now
  also invalidate the restart-required query.

## How it was verified

- New `tests/DbDataSync.Api.Tests/LibraryInstallTests.cs` (`Category=Integration`): `POST /api/libraries`
  for `Microsoft.Data.Sqlite` (the plan doc's own canary) → `GET /api/libraries` shows it, `resolves:
  true`, no restart in between → `DELETE` removes it. `POST` with no `factoryType` and none known →
  `400` naming the field. A Viewer is refused by all three mutations. `POST /api/drivers/from-catalog`
  for `mysql.generic` → `GET /api/drivers` lists it `source: "descriptor"`, `library:
  "mysql-connector"`, and `GET /api/libraries` shows `mysql-connector` with `usedBy: ["mysql.generic"]`
  — all still with no restart in between (see Decisions). A second `from-catalog` call for the same id
  → `409`. `DELETE /api/libraries/mysql-connector` while `mysql.generic` binds it → `409` (naming it in
  the body); `?force=true` → `204`, and a **fresh composition root over the same repo files**
  (`TestApiFactoryOnRepo`, new — simulates a real restart without spawning one) confirms `mysql.generic`
  no longer loads, host startup surviving the missing library exactly as `DriverLoader`'s `onError`
  contract promises.
- `AuthenticatedApiFactory` un-sealed further to support the new fixtures cleanly (no behavioral change,
  confirmed by every existing test using it still passing unchanged).
- New Playwright `admin-library-install.spec.ts`: a non-curated package (Dapper — a real, well-known
  package that happens not to be an ADO.NET provider, chosen only because it definitely isn't in
  `KnownLibraries`) needs a factory type before Install enables, opens the trust dialog, installs on
  confirm, and shows the restart banner; removing it (nothing depends on it) works with a plain
  confirm.
- `admin-drivers-libraries.spec.ts` (from 118) rewritten to install `mysql.generic` from the catalog
  list as its own first step (no trust dialog, since it's curated) rather than relying on
  `playwright.config.ts` seeding it via the CLI beforehand — a better fixture now that the real feature
  exists (see Decisions), and it still asserts everything 118 originally did once installed.
- Full `golden-path.spec.ts` (all 45 tests) green against the restructured fixture; a Docker build of
  the SDK-based image, run live, and `config library install` executed inside the running container
  over the real network — confirmed working end-to-end, not just built.
- Full `Category!=Integration` .NET suite green (1041+ tests).

## Decisions made

- **`LibraryRegistry.RegisterInstalled`/`Remove` mutate the live singleton in place, rather than
  requiring a restart before `GET /api/libraries` reflects a change.** The plan doc's own "How to
  verify" text is explicit — `resolves: true` immediately after `POST`, no restart mentioned — and a
  real registration (`DbProviderFactories.RegisterFactory`, process-wide and static) genuinely does
  resolve immediately once called, so `resolves: true` isn't a lie. Discovered by writing
  `LibraryInstallTests` first and watching it fail with "sequence contains no matching element": the
  injected `LibraryRegistry` singleton is built once at host startup and frozen, so a fresh install
  never appeared in it without this. Deliberately **not** re-running `LoadAll()` on every request or
  every install — `ArmResolver` unconditionally appends to a static, process-wide resolver list with no
  dedup, so a full rescan called more than once per process (which, unlike the one startup call, this
  phase now can trigger) would leak a duplicate `AssemblyDependencyResolver` per already-installed
  library on every call. `RegisterInstalled`/`Remove` touch only the one library actually being
  installed or removed, bounding the growth to real mutations rather than every page view.
- **`POST /api/drivers/from-catalog` also registers the new driver into the live `DriverRegistry`**,
  for the same reason and by the same evidence — the plan doc's test expects `GET /api/drivers` to
  list the new entry with no restart in between. This means a catalog-installed driver is actually
  *usable* immediately in this process, not just visible — which raises the question of what the
  restart banner is still protecting against. Left as: the banner is honest about *some* state
  (`TaskRunnerDllPath`, `StateEngine`, arbitrary config keys) never hot-patching, and conservatively
  flags every library/driver mutation too even though those specific ones, it turns out, already work
  without one in this process. Better to over-warn than to have the banner miss a case that still
  needs it.
- **The restart-required signal is a real server flag now** (`RestartRequiredState`, a marker file),
  not the client-only `useState` the Configuration screen shipped with in phase 81 — this was the
  phase's own explicitly flagged open question, resolved as leaned: "a lightweight server flag both
  screens read." Retrofitted into `AdminConfigService.Set`/`SetStateConnectionSecret` too, since the
  plan doc frames the new mutations as using "the same restart-required signal the Configuration screen
  uses" — which required making that signal real for the first time, not just reusing something that
  already existed.
- **`AdminCertificateService`'s mutations were not wired into the shared flag.** The plan doc names only
  the Configuration screen; Certificate is Windows-only and mostly untestable in this Linux sandbox
  (see its own test file's skip list), so extending the retrofit there was judged out of proportion to
  what could actually be verified here. `AdminCertificatePage.tsx` keeps its pre-existing, unaffected
  local-only banner.
- **`admin-drivers-libraries.spec.ts`'s fixture setup moved from a CLI shortcut to the real "Add"
  button.** Phase 118 seeded `mysql.generic` by shelling out to the CLI in `playwright.config.ts`
  because there was no web-triggered install yet; now that there is, and since this suite's `mysql.
  generic`/`mysql-connector` state has to exist exactly once per session (there is only one bundled
  catalog entry, and a second from-catalog call for the same id correctly refuses), the cleanest fix
  was to have 118's own spec install it as its first, self-contained step and run everything after
  against that — better coverage than a shortcut, and it removes the only reason
  `playwright.config.ts` still needed a `dotnet exec` of the CLI before the API's own webServer entry.
- **A `TestApiFactoryOnRepo` for "restart, but for a test"**: `TestApiFactory` gained a protected
  constructor overload accepting an existing repo root (and an `_ownsRepoRoot` flag so its `Dispose`
  doesn't delete a directory a different factory instance still owns) — the only way to prove "the
  driver now fails to load, and host startup survives that" without literally spawning a second OS
  process.

## What's explicitly out of scope / not built

- The slim runtime-only image and its pre-built catalog cache — **phase 121**.
- `factoryType` reflection-assist — **phase 122**.
- `driver.yaml` descriptor authoring (dialect / `typeMap`) or compiled-plugin install from the web — a
  later, separate planning doc.
- Background/async install with progress — install is synchronous; a slow `dotnet publish` holds the
  request. No problem observed in practice (Sqlite/mysql-connector-class installs land in seconds);
  revisit only if a real multi-package restore (Oracle, say) turns out to be slow enough to matter.
- Removing a *driver* (only libraries get a `DELETE`) — a driver's own descriptor still only goes away
  through `config driver uninstall` or by hand.

## Open questions, resolved

- Synchronous `dotnet publish` inside a request, with a generous framework default timeout rather than
  a job+poll — measured against the two real packages this phase's tests actually install
  (`Microsoft.Data.Sqlite`, `MySqlConnector`/`mysql-connector`, and `Dapper`), all landing in single-digit
  seconds. No evidence yet that a heavier restore (Oracle's managed client, say) would need anything
  more — left as a real, if unexercised, risk rather than building machinery for it now.
- The restart-required signal is a file touch in the repo root, read by both screens — built as leaned.
- `from-catalog` refuses when a driver with that id already exists (rather than re-pointing it) — built
  as leaned, and asserted directly (`FromCatalog_RefusesWhenADriverWithThatIdAlreadyExists`).
