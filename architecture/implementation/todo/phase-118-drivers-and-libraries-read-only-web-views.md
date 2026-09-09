# Phase 118 — read-only Drivers and Libraries screens (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The web endpoints and screens*, Decision 6. Depends on phase 116 (library names, descriptor
reference) and phase 117 (the catalogs, for the "add" affordances). No mutation — nothing this phase
adds writes to disk.

## Why

Phase 109d made the connection editor's engine picker data-driven off `GET /api/drivers`, but there
is still no screen where an operator can *see* what drivers and libraries are installed, what each
driver binds, or what an unavailable engine would take. This phase adds that visibility, and closes
a gap: `DriversController` hard-codes `source: "descriptor"` for every non-built-in driver, so a
compiled plugin (phase 109e) is mislabelled.

## What this phase builds

### API

- **`GET /api/drivers`** (extend `DriverSummary`): add `library` (the bound library id, `null` for a
  built-in) and `capabilities` (reader / staging / writer kind-name lists, from
  `DriverRegistry.Describe` — already computed, just not on this DTO). `source` gains `"compiled"`
  as a real value — decide built-in vs. descriptor vs. compiled from `DriverIds` membership plus
  which manifest file backs the driver, rather than the current two-way guess.
- **`GET /api/libraries`** → `[{ id, packages: [{ id, version }], factoryType, resolves, usedBy:
  [driverId…], curated }]`. `resolves` is a real `LibraryRegistry.GetFactory` probe (same as
  `config library list`). `usedBy` is every descriptor/compiled driver whose `library` is this id.
  `curated` = the id matches a `KnownLibraries` entry.
- **`GET /api/known-libraries`**, **`GET /api/known-drivers`** → the phase-117 catalogs
  (`{ id, displayName, description, packageId?, boundLibrary? }`), for the "available to add"
  lists.
- New `LibrariesController`; `DriversController` extended. All `[Authorize(Policies.Admin)]`,
  stated explicitly per the `AdminConfigController` precedent (this screen reveals what's installed
  — an admin-only bar, like the config screen).

### SPA

- `components/AdminTabs.tsx` gains **Drivers** and **Libraries** `NavLink`s (→ four Admin tabs:
  Configuration | Certificate | Drivers | Libraries — Decision 6).
- `App.tsx` routes `/admin/drivers`, `/admin/libraries`.
- `pages/AdminDriversPage.tsx` — a table grouped by `source`; built-ins marked and inert; each
  descriptor/compiled row links its library; a capability summary per row. An "Add a driver" panel:
  the `known-drivers` list (rendered, with a disabled "Add" — enabled in 120) plus the copyable
  `dbdatasync config driver install <id> --library <name> --from <catalog>` command and a link to
  the driver doc.
- `pages/AdminLibrariesPage.tsx` — installed libraries with `usedBy`, `resolves` (a status badge),
  and `curated` (a "vetted" pill). A disabled "Install a library" affordance (enabled in 119/120).
- `api/client.ts` + `api/hooks.ts`: `useLibraries()`, `useKnownLibraries()`, `useKnownDrivers()`;
  extend `useDrivers()` / the `DriverSummary` type in `api/types.ts`. No polling — these lists
  change at most once per deployment (same call `useDrivers()` already made).

## How to verify when built

- New `tests/DbDataSync.Api.Tests/LibrariesControllerTests.cs` + additions to the drivers-endpoint
  tests: `GET /api/libraries` reports an installed library with `usedBy` populated from a real
  descriptor; `GET /api/drivers` returns `library` and `capabilities` for a descriptor driver and
  `source: "builtin"` for the three built-ins; a compiled fixture driver (phase 109e's
  `LoaderTestFixture`) reports `source: "compiled"`.
- Playwright: a new `admin-drivers-libraries.spec.ts` — the Drivers tab lists the three built-ins
  (and any descriptor the fixture repo has), the Libraries tab lists an installed library; the
  "Add" affordances are present but disabled.
- `golden-path.spec.ts` test 31 (`connection-driver-select`) still green — the picker is untouched.
- `npm run build` / `lint` clean.

## What this phase does not build

- Any `POST`/`DELETE` — install and remove are phase 120.
- NuGet search — phase 119.
- Enabling the "Add" buttons.
- Editing a `driver.yaml` descriptor (dialect / `typeMap`) through the UI — a later, separate
  planning doc.

## Open questions to resolve during implementation

- Whether `capabilities` on `GET /api/drivers` duplicates enough of
  `GET /api/connections/{name}/capabilities` to matter. It doesn't — that endpoint is
  connection-scoped and resolves host readers; this is a driver-level summary for a catalogue view.
  Confirm the DTOs don't accidentally converge.
- Whether Drivers and Libraries should be one screen with two sections after all, once the real
  layout is in front of us (Decision 6 leans two tabs but flags this).
