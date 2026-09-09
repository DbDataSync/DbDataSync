# Phase 118 — read-only Drivers and Libraries screens

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The web endpoints and screens*, Decision 6. Depended on phase 116 (library names, descriptor
reference) and phase 117 (the catalogs, for the "add" affordances). No mutation — nothing this phase
added writes to disk.

## What this built

### API

- **`GET /api/drivers`** (extended `DriverSummary`): added `library` (the bound library id, `null` for
  a built-in or compiled plugin) and `capabilities` (a kind-name-only `DriverCapabilitySummary` —
  reader/staging/writer kind lists, from `DriverRegistry.Describe`). `source` gained `"compiled"` as a
  real value. New `DriverDescriptorScanner` (`DbDataSync.Api/Services`) reads `<repo>/drivers/*`
  read-only — the same `driver.yaml`/`driver.json` distinction `DriverLoader` and the CLI's `driver
  list` already make, but for display, tolerating a bad descriptor by omitting it rather than throwing.
  `DriversController` stayed at `[Authorize(Policies.Viewer)]`, unchanged — the connection editor's
  engine picker (phase 109d) is reachable by a Viewer, confirmed by golden-path test 31 staying green.
- **`GET /api/libraries`** → `[{ id, packages, factoryType, resolves, usedBy, curated }]`, via new
  `LibrariesService`. `resolves` is a real `LibraryRegistry.GetFactory` probe; `usedBy` comes from the
  same `DriverDescriptorScanner`; `curated` checks `KnownLibraries.TryGetById`.
- **`GET /api/known-libraries`**, **`GET /api/known-drivers`** → the phase 117 catalogs, reshaped into
  `KnownLibrarySummary`/`KnownDriverSummary`. `KnownDriverEntry` (117) gained a `Description` field for
  this — the API shape the plan doc sketched needed one and 117 hadn't added it.
- New `LibrariesController` (`/api/libraries`, `/api/known-libraries`, `/api/known-drivers`), all
  `[Authorize(Policies.Admin)]` per the `AdminConfigController` precedent — this screen reveals what's
  installed on the host, an admin-only bar.

### SPA

- `components/AdminTabs.tsx` gained **Drivers** and **Libraries** `NavLink`s (four Admin tabs total).
- `App.tsx` routes `/admin/drivers`, `/admin/libraries`.
- `pages/AdminDriversPage.tsx` — a table of every registered driver (id, display name, source, bound
  library, capability kind list) plus an "Add a driver" panel listing `known-drivers` entries, each
  with a disabled "Add" button (120 wires it) and a copyable
  `dbdatasync config driver install <id> --library <name> --version <v> --from <catalogId>` command.
- `pages/AdminLibrariesPage.tsx` — installed libraries with packages, a `resolves` status badge, a
  "vetted" pill for `curated`, and `usedBy`. A disabled "Install a library" button (119/120 enable it).
- `api/client.ts` + `api/hooks.ts`: `useLibraries()`, `useKnownLibraries()`, `useKnownDrivers()`;
  extended `useDrivers()`'s `DriverSummary` type. No polling on any of them — same reasoning
  `useAdminConfig` already documents.

## How it was verified

- New `tests/DbDataSync.Api.Tests/LibrariesControllerTests.cs` (`Category=Integration` — a real library
  install): a Viewer is refused by all three new endpoints; an Admin sees the installed library with
  `usedBy` populated from a real on-disk descriptor and `curated: true`; both catalog endpoints return
  the phase 117 entries.
- New `tests/DbDataSync.Api.Tests/DriversControllerTests.cs` (`Category=Integration`, same reason):
  built-in drivers report `source: "builtin"` and `library: null` with non-empty capability lists; a
  descriptor driver (seeded via a new `LibrariesAdminApiFactory`/`…NoAuth`) reports its bound library
  and `source: "descriptor"`; a compiled fixture driver (a new `CompiledDriverApiFactory`, reusing
  `DbDataSync.Drivers.Loader.Tests`' `FixturePublisher` via a new cross-test-project reference) reports
  `source: "compiled"` and `library: null`.
- `AuthenticatedApiFactory` un-sealed (was `sealed`, nothing needed to subclass it before) so those two
  new factories could extend it — a one-line, behavior-neutral change confirmed by the existing
  `AdminConfigControllerTests` etc. still passing unchanged.
- New Playwright `admin-drivers-libraries.spec.ts`: the Drivers tab lists the three built-ins (each
  `Source` cell reading "Built-in") and a seeded `mysql.generic` descriptor row naming its
  `mysql-connector` library, with the catalog's "Add" button present and disabled; the Libraries tab
  lists that same library as resolving and vetted, naming `mysql.generic` in `usedBy`, with "Install a
  library" present and disabled.
- Full `golden-path.spec.ts` (all 45 tests, including test 31 — `connection-driver-select`) green, plus
  a spot-check of `duckdb-query-source.spec.ts` — both exercise the `webServer`/`globalSetup` machinery
  this phase's Playwright fixture changed (see Decisions).
- Full `Category!=Integration` .NET suite green (1041+ tests); `npm run build` and `npm run lint` clean
  (lint's pre-existing warnings are unrelated to this phase's files).

## Decisions made

- **Libraries and drivers load once at API composition-root startup and never hot-reload** — this was
  already true (it's why phases 120's plan calls out a restart-required signal), but it surfaced as a
  real bug in this phase's own Playwright fixture: seeding the scratch repo's descriptor driver from
  `global-setup.ts` (which Playwright runs *concurrently with*, not strictly before, the `webServer`
  entries) was too late — the API's hosted services already resolve an empty `LibraryRegistry`/
  `DriverRegistry` before `global-setup.ts` gets a turn to run, confirmed empirically by a deliberate
  8-second-sleep probe. Fixed by moving the scratch-repo clear + CLI-seed into synchronous top-level
  code in `playwright.config.ts` itself — code Playwright must finish evaluating before it can even
  read the `webServer` array out of the config object, which is what actually guarantees the ordering.
  `global-setup.ts` keeps only the SQL Server seeding, which never depended on this ordering.
- **`AuthenticatedApiFactory` un-sealed** rather than duplicating its session/auth wiring into two new
  factory classes — a smaller, safer diff than copying ~40 lines twice.
- **`GET /api/drivers` stays `Policies.Viewer`**, not moved to `Admin` — the plan doc's "All
  `[Authorize(Policies.Admin)]`" language read as covering every endpoint this phase touches, but taking
  it literally would have 403'd the connection editor's engine picker for a Viewer, which is exactly
  what golden-path test 31 exists to catch. Read instead as covering only the genuinely new endpoints
  (`LibrariesController`'s three), which is what got built.
- **`KnownDriverEntry` gained a `Description` field**, extending phase 117's record after the fact —
  its own `GET /api/known-drivers` shape needed one and 117 hadn't anticipated the web surface closely
  enough to add it up front.

## What's explicitly out of scope / not built

- Any `POST`/`DELETE` — install and remove are phase 120.
- NuGet search — phase 119.
- Enabling the "Add" buttons.
- Editing a `driver.yaml` descriptor (dialect / `typeMap`) through the UI — a later, separate planning
  doc.

## Open questions resolved during implementation

- `capabilities` on `GET /api/drivers` does not meaningfully duplicate
  `GET /api/connections/{name}/capabilities` — confirmed: that endpoint is connection-scoped and
  resolves host readers with full per-Kind parameter detail; this phase's `DriverCapabilitySummary` is
  kind-names only, for a catalogue view. The two DTOs share no type.
- Drivers and Libraries stayed **two separate tabs**, not one screen with two sections — the real
  layout (a driver's own row wanting to link to its bound library's row, a library wanting to show
  every driver using it) reads more clearly as two focused tables than one crowded one.
