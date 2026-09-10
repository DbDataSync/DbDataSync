# Phase 122 — `factoryType` reflection-assist for non-catalog libraries

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*Follow-on work* → *`factoryType` reflection-assist*, Decision 5. Depends on phase 120 (confirmed
already in `done/` before starting).

## What this built

### `src/DbDataSync.Libraries/FactoryTypeReflector.cs` (new)

`FactoryTypeReflector.Discover(libDir)` scans every top-level `*.dll` in a restored library's `lib/`
directory for a public, non-abstract `System.Data.Common.DbProviderFactory` subclass that also declares
a public static `Instance` field or property — the shape every bundled provider in `KnownLibraries`
already has. Returns `Found` (exactly one match, with its assembly-qualified name), `Ambiguous` (more
than one), or `NotFound` (none).

- **Inspection-only**: every assembly loads into a `System.Reflection.MetadataLoadContext`, never the
  process's own `AssemblyLoadContext` — nothing in a scanned assembly ever executes, module
  initializers and static constructors included. The `Instance` check is metadata-only too (`GetField`/
  `GetProperty`, never reading the value), so requiring it costs nothing in trust even though it does
  real work: it's what keeps an incidental `DbProviderFactory` subclass with no working singleton (an
  abstract-ish helper type a package happens to ship alongside the real one) from being offered as if it
  were the entry point.
- **The resolver** is just the restored closure's own DLLs plus `RuntimeEnvironment.GetRuntimeDirectory()`
  — no `.deps.json` parsing. This was the open question in the plan doc, and it's resolved: verified
  empirically (see below) against three real restored closures (MySqlConnector, Npgsql,
  `System.Data.SqlClient`), none of which collides by simple assembly name with anything in the shared
  framework directory, and `System.Data.Common` (where `DbProviderFactory` lives) always resolves from
  there directly. A `.deps.json`-driven resolver would have been strictly more machinery for no observed
  benefit.

### `LibraryInstaller.InstallAsync` — `factoryType` is now nullable

Restore always runs first, exactly as before. When `factoryType` is null afterward, `Discover` runs
against the freshly-restored `lib/` directory:

- `Found` → that value is used, silently — no separate confirmation round-trip, since `POST
  /api/libraries` and `config library install` are already a single synchronous call that only
  proceeds past the trust-confirmation step phase 120 built. What "the operator confirms" (the plan
  doc's phrasing) resolves to in this single-shot architecture is the manifest's `factoryType` field
  being visible in the response and in `GET /api/libraries` afterward — not a second gate.
- `Ambiguous`/`NotFound` → the just-restored `libraries/<id>/` directory is deleted (nothing left
  half-installed — the same "nothing installed" outcome any other failed install already leaves) and an
  `InvalidOperationException` is thrown naming which case it was, caught by both callers exactly like
  any other install failure.

### `config library install` and `POST /api/libraries`

Both callers' pre-restore "no known factory type → refuse" checks are gone. `factoryType` still
resolves the same way it always did first — an explicit value, then a `KnownLibraries` guess — but a
`null` result now flows into `LibraryInstaller.InstallAsync` instead of failing before the restore ever
runs. `dbdatasync config driver install` (the `--from`/library-binding path) was deliberately **not**
touched — the plan doc scoped this assist to `config library install` alone.

### The Libraries admin screen (`AdminLibrariesPage.tsx`)

The non-curated install form's Factory type field is no longer required before the Install button
enables — its placeholder now says "leave blank to try auto-detect". On a successful install with the
field left blank, the confirmation line shows what was actually detected (`Installed — detected factory:
X.`), reading it straight off the manifest the `POST` response already carried — no new endpoint needed.

## How it was verified

- **`tests/fixtures/DbDataSync.Libraries.FactoryFixtureOne/Two/ModuleInit`** (new, three small
  throwaway projects, unreferenced by the solution build — same "publish on the fly, point the code
  under test at the output" pattern `DbDataSync.Drivers.LoaderTestFixture` already established):
  - `One` — a single qualifying `DbProviderFactory` subclass, plus an internal one, an abstract one, and
    an unrelated public type, all in the same assembly, to prove the scan is actually filtering on
    public+non-abstract+assignable+has-`Instance`, not just "any type in the DLL".
  - `Ambiguous` — two qualifying subclasses in one assembly.
  - `ModuleInit` — one qualifying subclass, plus a `[ModuleInitializer]` that throws the instant the
    assembly is *actually* loaded. `FactoryTypeReflectorTests.Discover_DoesNotExecuteAModuleInitializer`
    passing (rather than the exception escaping the test) is the proof this stays inspection-only.
  - New `tests/DbDataSync.Libraries.Tests/FixturePublisher.cs` (a small generalization of
    `DbDataSync.Drivers.Loader.Tests.FixturePublisher` — keyed by project name, one publish per name,
    cached for the run) and `FactoryTypeReflectorTests.cs` (5 tests, all passing) drive them.
- **`LibraryLoadTests`** (2 new tests): `LibraryInstaller.InstallAsync(..., factoryType: null)` against a
  real `MySqlConnector` restore discovers `MySqlConnector.MySqlConnectorFactory, MySqlConnector` by
  reflection alone, and the resulting factory opens a real connection to the MySQL container and runs
  `SELECT 1` — `LibraryInstaller` never consults `KnownLibraries` itself (only its callers do), so
  calling it directly with `factoryType: null` **is** "as if MySqlConnector were not in the catalog", at
  the exact layer that owns the mechanism. A second test installs `Newtonsoft.Json` (real, restorable,
  no `DbProviderFactory` anywhere in it) with `factoryType: null` and confirms it throws naming
  `DbProviderFactory`, and that `libraries/Newtonsoft.Json/` does not exist afterward.
- **`LibraryCommandTests`**: the old `Install_WithoutAKnownFactoryType_RequiresOneExplicitly` test used a
  package id (`SomeUnknownPackage`) that doesn't exist on NuGet at all, so it exercised a pre-restore
  check that no longer exists — replaced with a real-restore equivalent using `Newtonsoft.Json`. A new
  test installs `System.Data.SqlClient` (a real ADO.NET provider, deliberately not one of the seven
  bundled catalog entries) with no `--factory-type` and confirms the CLI discovers and prints
  `System.Data.SqlClient.SqlClientFactory, System.Data.SqlClient`.
- **`LibraryInstallTests`** (API): the old 400 test used a nonexistent package id, exercising the same
  now-removed pre-restore check — replaced with `Newtonsoft.Json`, expecting a 400 naming
  `DbProviderFactory` and confirming nothing shows up in `GET /api/libraries` afterward; a new test
  `POST`s `System.Data.SqlClient` with no `factoryType` and confirms it installs, resolves, and is
  correctly reported `curated: false`.
- `dotnet build DbDataSync.slnx -c Release` clean. Full solution test suite run (including all
  `Category=Integration` tests, this sandbox having real network/Docker access) — see the commit for the
  final pass/fail count.
- `npm run build` and `npm run lint` on `DbDataSync.Web` — clean; no new lint findings introduced by the
  `AdminLibrariesPage.tsx` change. No Playwright e2e coverage exists yet for the Libraries admin page (a
  pre-existing gap, not one this phase introduced), so the SPA change was verified by typecheck + lint
  only, not a live browser pass — flagged rather than silently assumed.

## Decisions made

- **The resolver is glob-the-directory, not `.deps.json`-driven** — the plan doc's own open question,
  resolved empirically rather than by inspection: three real closures (MySqlConnector, Npgsql,
  `System.Data.SqlClient`) all resolved cleanly with zero simple-name collisions against the shared
  framework directory. `.deps.json` parsing would only earn its keep if a real collision were ever
  observed; none was.
- **`Instance` field/property presence is checked and gates the result**, resolving the plan doc's other
  open question ("whether to also verify... before offering it") as yes — but purely via metadata
  (`GetField`/`GetProperty`), never by reading the field's actual value, which would require a real
  (executing) load and contradict the inspection-only guarantee this phase's own test proves. A
  candidate missing `Instance` is treated the same as no candidate at all.
- **No second "confirm the pre-fill" round trip** — the plan doc's phrasing ("pre-fill... the operator
  confirms") reads naturally as a two-step UI flow, but phase 120's `POST /api/libraries` is a single
  synchronous call that already restores and installs in one shot; a second gate after a real restore
  had already completed would mean either doing the restore twice or holding installed-but-unconfirmed
  state, neither of which phase 120 has a place for. The chosen equivalent — install completes, the
  detected `factoryType` is visible in the response and the list afterward — gives the same visibility
  without a new state machine.
- **`config driver install` untouched** — the plan doc's own "what this builds" scoped the assist to
  `config library install`; `driver install --from`'s pre-restore factory-type check stays exactly as
  it was.

## What this does not build

- Guessing anything beyond the factory type — connection-string key names, dialect, and type maps stay
  the operator's problem for a non-catalog engine, exactly as the plan doc said.
- Removing the manual `factoryType` field from the SPA form — it stays, just no longer required
  up front.
- Any change to `config driver install`'s own factory-type requirement.

## Open questions — resolved

1. **Whether `.deps.json` is needed to build the `MetadataLoadContext` resolver, or a simpler directory
   glob suffices** — resolved: the glob suffices, verified against three real package closures.
2. **Whether to verify the discovered factory actually instantiates (`Instance` non-null) before
   offering it** — resolved as "check for presence, not value": reading the value would require a real
   execution this phase deliberately never does.
