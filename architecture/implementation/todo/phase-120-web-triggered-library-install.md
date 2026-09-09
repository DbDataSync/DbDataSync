# Phase 120 — web-triggered library install / remove; SDK becomes the default image (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The web endpoints and screens*, §*Trust, build environment, and restart*, Decision 1. Depends on
phases 116–119. Last phase of the committed arc; phases 121–122 are follow-on.

## Why

Phases 118–119 make drivers and libraries visible and searchable but read-only — an admin still
drops to the CLI to install. This phase closes the loop: install a library (or a whole catalog
driver) from the console, gated by admin authz and, for a non-catalog package, an explicit trust
confirmation.

## What this phase builds

### The default Docker image moves to an SDK base

`LibraryInstaller` shells out to `dotnet publish` (phase 109c established that `publish`, not
`restore`, is what produces the flat `lib/` with native assets and a `.deps.json`). That needs the
.NET SDK. Every non-container deployment already has it (`dbdatasync` is a `dotnet tool`, and
`dotnet tool install` requires the SDK). The container is the only gap.

- `Dockerfile`: the final stage moves from `mcr.microsoft.com/dotnet/aspnet:10.0` to
  `mcr.microsoft.com/dotnet/sdk:10.0`. The `build` stage is already on `sdk:10.0`.
- The image grows (~250 MB → ~750 MB uncompressed base, before DbDataSync's own ~150 MB). Accepted —
  a follow-on phase (121) adds a slim runtime-only image for shops that need it.
- CI: any image-size assertion updated; the container smoke test (`docker compose` up + `health`)
  confirms the SDK image still starts and serves.

### API

- **`POST /api/libraries`** ← `{ packageId, version, factoryType?, source? }`. Runs
  `LibraryInstaller.InstallAsync` **synchronously** in-process — the SDK is present by construction,
  so no probe, no "pending" state (that's phase 121's problem on the slim image). Writes
  `library.json`, returns the manifest or a structured error (a failed `dotnet publish` surfaces its
  stderr tail, not a bare 500). `factoryType` required when `packageId` isn't in `KnownLibraries`.
- **`POST /api/drivers/from-catalog`** ← `{ knownDriverId, version }`. Installs the catalog entry's
  bound library at `version`, writes the bundled descriptor (id + displayName + `library:` filled
  in). The one-click path.
- **`DELETE /api/libraries/{id}`** — refused (`409`, with the using-driver list) when `usedBy` is
  non-empty; `?force=true` overrides. In-use is in-use regardless of `resolves`.
- All `[Authorize(Policies.Admin)]`. Each mutation sets the same restart-required signal the
  Admin → Configuration screen uses.

### SPA

- Enable the Install button on `AdminLibrariesPage` (from a search result + version, or manual
  entry) and the "Add" button on `AdminDriversPage`'s catalog list.
- A **non-catalog install** (packageId not in `known-libraries`) opens a confirmation dialog:
  "Installing this package runs its code inside the DbDataSync host, with the host's privileges —
  the same trust as a hook or a script. Only continue for a package you have vetted." Catalog
  installs skip the dialog.
- After any install/remove: the `RestartRequiredBanner` (reused from `AdminConfigPage`), because
  libraries and drivers load at composition-root startup. Note in the banner copy that a running
  replication's next run (a fresh `TaskRunner` process) already picks up the change — only the API
  needs the restart.
- Remove button on each library row, disabled when `usedBy` is non-empty (with a tooltip naming the
  drivers), a force path behind a confirm.

## How to verify when built

- `tests/DbDataSync.Api.Tests/LibraryInstallTests.cs` (`Category=Integration` — needs the SDK and a
  feed): `POST /api/libraries` for `Microsoft.Data.Sqlite` (the parent doc's canary — a real
  maintained provider, no container) → `GET /api/libraries` shows it, `resolves: true` → `DELETE`
  removes it. `POST` for a package with no `factoryType` and none known → `400` naming the field.
- `POST /api/drivers/from-catalog` `{ knownDriverId: "mysql.generic", version: "2.4.0" }` →
  `GET /api/drivers` lists `mysql.generic` with `source: "descriptor"`, `library: "mysql-connector"`;
  `GET /api/libraries` shows `mysql-connector` with `usedBy: ["mysql.generic"]`.
- `DELETE /api/libraries/mysql-connector` while `mysql.generic` binds it → `409`; `?force=true` →
  removed, and `mysql.generic` now fails to load (logged, host survives).
- Playwright `admin-library-install.spec.ts`: add SQLite via the UI (manual entry, since search may
  be offline in CI), see the trust dialog, confirm, see it listed and the restart banner; add MySQL
  from the catalog list, see no trust dialog, see the driver appear on the Drivers tab.
- The full non-integration suite and the container smoke test green with the SDK image.

## What this phase does not build

- The slim runtime-only image and its pre-built catalog cache — **phase 121**.
- `factoryType` reflection-assist — **phase 122**.
- `driver.yaml` descriptor authoring (dialect / `typeMap`) or compiled-plugin install from the web —
  a later, separate planning doc.
- Background/async install with progress — install is synchronous; a slow `dotnet publish` holds the
  request. Acceptable for an admin action; revisit if it's a problem.

## Open questions to resolve during implementation

- Whether a synchronous `dotnet publish` inside a request is acceptable or needs a job + poll from
  the first version. Leaning: synchronous, with a generous server timeout; measure a real
  multi-package restore (Oracle) before adding machinery.
- Where the restart-required signal lives if an operator installs via the API and a *different*
  admin is watching the Configuration screen — today it's client-only state. Leaning: a lightweight
  server flag (a file touch in the repo root, cleared on start) both screens read, small enough to
  fold in here.
- Whether `from-catalog` should refuse if a driver with that id already exists, or offer to
  re-point it. Leaning: refuse, name the existing one.
