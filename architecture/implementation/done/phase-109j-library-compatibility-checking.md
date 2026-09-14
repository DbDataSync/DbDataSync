# Phase 109j — library compatibility checking

**Status**: Complete.
**Plan reference**: answers 109h's "Version fidelity at runtime, not compile time" risk and 109i's
identical one, both inherited from `nuget-loaded-drivers.md`'s own "Version conflicts with the host."

## Why, unchanged

`ExcludeAssets="runtime"` (109h/109i) means the exact library version a driver actually runs against is
whatever's installed at `<repo>/libraries/<id>/lib/` — which can differ from the version the driver was
compiled against. A genuine breaking change in the installed version surfaces as a runtime
`MissingMethodException`/`TypeLoadException` at first use, not a build error. This phase builds two real
techniques that close the gap: a static, no-execution IL-surface check (item 2/3 below), and a deep,
connection-scoped check that actually stages and writes real rows through the driver's real pipeline
(item 4).

## What this built

### 1. `IDriver.RequiredLibraryId`

A default interface member on `IDriver` (`src/DbDataSync.Drivers.Abstractions/IDriver.cs`):

```csharp
string? RequiredLibraryId => null;
```

`MsSqlDriver.RequiredLibraryId => "microsoft-data-sqlclient"`, `PostgresDriver.RequiredLibraryId =>
"npgsql"`, `DuckDbDriver.RequiredLibraryId => "duckdb"` — each a literal restating the exact id
`MsSqlStateDialect.LibraryId`/`PostgresStateDialect.LibraryId`/109i's own `KnownLibraries` entry already
use, not a shared constant (these driver projects don't otherwise depend on `DbDataSync.State`).

### 2. Static surface checking — Mono.Cecil to extract, `MetadataLoadContext` to check

Two new small types in `src/DbDataSync.Libraries/`:

- **`LibrarySurfaceExtractor`** — reads a driver's own already-built DLL via Mono.Cecil
  (`ModuleDefinition.GetMemberReferences()`/`GetTypeReferences()`, `ReadingMode.Deferred`, never
  executed) and lists every distinct member/type it references whose declaring type's assembly matches
  the required library's simple name. Each extracted `UsedMember` carries `DeclaringTypeFullName` (Cecil's
  `/`-nested-type separator normalized to reflection's `+`), a `Kind` (`Type`/`Field`/`Method`), the
  member name, and (for methods/constructors) its parameter count.
- **`LibrarySurfaceChecker`** — loads the candidate installed library purely as metadata via
  `System.Reflection.MetadataLoadContext`, with a `PathAssemblyResolver` seeded from every DLL in the
  library's own `lib/` directory *plus* every DLL in `RuntimeEnvironment.GetRuntimeDirectory()` — the
  identical, already-proven resolver pattern `FactoryTypeReflector` (phase 122) uses for the same reason.
  For each `UsedMember`, confirms the declaring type exists in the candidate and (for a field/method) that
  a member with the same name and, for methods, the same parameter count exists on it.
- **`DriverLibraryCompatibility.Check(driver, registry, repoRoot)`** — the facade both call sites below
  use: null when there's nothing to check yet (no `RequiredLibraryId`, not installed, or installed but
  still `PendingRestore` with no `lib/` on disk), otherwise a `DriverLibraryCompatibilityResult` naming
  compatibility and any missing members.

**This never touches `AssemblyLoadContext.Default`.** `MetadataLoadContext` is its own disposable,
isolated context — confirmed by design (it never JITs or executes) and by the extensive real-world runs
below, none of which left anything resident.

### 3. Wired into `library install`/`sync` and `config check`

- **`LibraryCommand.cs`** (`PrintCompatibilityWarnings`) — after a successful install or sync, for every
  `BuiltInDrivers.All` entry whose `RequiredLibraryId` matches, runs the check and prints any missing
  members immediately, before anything tries to use the library for real.
- **`ReadinessChecks.cs`** (`LibraryCompatibilityCheck`, added to the `Checks` list right after
  `LibrariesAndDriversCheck`) — always `Warn`, never `Fail`. **Per-installed-library, not per-driver**
  (open question 4, resolved as the doc's own lean): results are grouped by `LibraryId` first, so a
  library used by more than one driver is reported once, naming every driver it backs. The
  grouping/formatting logic is pulled into a separate, directly-testable `Summarize` method — see
  "How it was verified."
- **`BuiltInDrivers.All`** (new, `src/DbDataSync.Cli/BuiltInDrivers.cs`) — `[new MsSqlDriver(), new
  PostgresDriver(), new DuckDbDriver()]`, the CLI's own narrow answer to "which built-in drivers exist"
  for these two call sites; not shared with `DbDataSyncHost.Build()`/`TaskRunner`'s `Program.cs`, which
  build a live `DriverRegistry` with scripting for a different reason.

### 4. The deep, connection-scoped check

- **`dbdatasync config library validate <id> --connection <name> [--repo <root>]`** — a real, documented
  verb on `LibraryCommand.cs` (confirmed `InternalCommand.cs` is exactly what its own doc comment says:
  build-only, absent from `Help.Print` — the wrong home). Resolves the connection through the identical
  `ConfigRepository` + `SecretStore` path every other CLI command uses. Refuses cleanly (not a crash) when:
  the connection doesn't exist, its driver isn't registered among `BuiltInDrivers.All`, the named library
  id doesn't match that driver's `RequiredLibraryId`, or the driver has no staging provider/writer
  registered at all (`DuckDbDriver` — both lists are empty; this is `DuckDb`'s only unfixed. It has a
  `RequiredLibraryId`, so the static check still runs for it, but nothing to deep-validate).
- **`LibraryValidationRunner.RunAsync`** (new, `src/DbDataSync.Cli/LibraryValidationRunner.cs`) — the
  mechanism:
  1. Resolves the credential and calls `driver.CreateConnection` inside a `try` that catches
     `DbException`/`InvalidOperationException`/`FileNotFoundException`/`FileLoadException`/
     `BadImageFormatException`/`TypeLoadException`/`MissingMethodException`/`SecretNotFoundException` — a
     real bug found running this for real (see below): constructing the connection, not just opening it,
     is where a genuinely broken library surfaces, and an unhandled exception here would crash the whole
     (already-isolated) child process instead of reporting a clean `Failed` result.
  2. Sweeps any previous run's stale `DbDataSync_LibraryValidation_*` table it can still see (via the
     driver's own `ListTablesAsync`) before creating a new one — open question 3, resolved **yes**,
     best-effort (a sweep failure doesn't fail this run).
  3. Creates a scratch table `DbDataSync_LibraryValidation_<guid:N>` — `Id INT/INTEGER NOT NULL PRIMARY
     KEY`, `Name NVARCHAR(200)/VARCHAR(200) NULL`, `UpdatedAt DATETIME2/TIMESTAMP NULL` (MsSql/other),
     every identifier quoted through the driver's own `SqlDialect.QuoteIdentifier` when it implements
     `IDialectProvider` (both `MsSqlDriver` and `PostgresDriver` do). A `CREATE TABLE` failure is reported
     by name, naming the missing CREATE TABLE privilege specifically, not a generic staging error.
  4. Resolves `driver.StagingProviders[0]`/`driver.Writers[0]` — the **first-registered** entries (open
     question 2, resolved: "first registered" is what this phase uses; not revisited as a per-driver
     declared default, since both `MsSqlDriver` and `PostgresDriver`'s own registration order already puts
     the real, engine-typed pipeline first — `MsSqlStagingTableProvider`/`MsSqlMergeWriter` for MsSql,
     `BatchInsertStagingProvider`/`DeleteInsertWriter` for Postgres, which has no bespoke staging provider
     of its own but whose writers still do real `NpgsqlDbType` typed binding).
  5. Hand-builds 3 synthetic `ChangeRow`s (`Id`/`Name`/`UpdatedAt`, all `Insert`) and matching
     `ColumnMapping`/`CachedColumn` values, and calls `StageAsync` then `ApplyAsync` for real, inside a
     `try` catching the same broad exception set — this is the exact scenario the phase exists for: a
     member removed from the installed library surfaces here as `MissingMethodException`, reported by
     name rather than crashing.
  6. Confirms the row count landed via a real `SELECT COUNT(*)`, then `CleanupAsync` and `DROP TABLE IF
     EXISTS` in a `finally` — deterministic teardown even on failure, both steps best-effort so a second
     failure during cleanup never hides the first, real one.
- **Spawned as a child process from the API** — `LibraryValidationLauncher`
  (`src/DbDataSync.Api/Services/LibraryValidationLauncher.cs`), reusing `ProcessSupervisor.BuildStartInfo`'s
  `dotnet exec <dll>` *shape* (not its literal method — that one is hardcoded to `DbDataSync.TaskRunner`'s
  own args/environment-variable protocol): `dotnet exec <CliDllPath> config library validate <id>
  --connection <name> --repo <repoRoot>`, awaited synchronously (a validate run is one bounded check, not
  an unbounded queue-drain worth a fire-and-forget). `ApiOptions.CliDllPath` resolves the same
  "beside-the-running-assembly-first, dev-layout-fallback" way `TaskRunnerDllPath` already does — in
  production this is trivial, since `dotnet /app/DbDataSync.Cli.dll serve` (the Dockerfile's own
  entrypoint) hosts `DbDataSyncHost.Build()` *inside that same process*, so `AppContext.BaseDirectory` IS
  already `DbDataSync.Cli.dll`'s own directory. `libraryId`/`connectionName` travel as plain command-line
  arguments (never secrets — the credential is resolved *inside* the child, identically to every other CLI
  command); the child's own `SecretStore("DbDataSync", true)` reaches the same durable, cross-process
  secret store a real deployment's OS keyring (or file fallback) already is.
- **`POST /api/connections/{name}/validate-library`** (`ConnectionsController.ValidateLibrary`) — resolves
  the connection's driver, 400s cleanly if it has no `RequiredLibraryId`, otherwise calls
  `LibraryValidationLauncher.RunAsync` and returns `LibraryValidationReport(Succeeded, LibraryId, Output)`
  — `Output` is the spawned CLI's own message, verbatim.

### 5. Where this surfaces in the product

- **Connections page (SPA)**: a "Validate library" button beside "Test connection" (shown only when
  `DriverCapabilities.SupportsLibraryValidation` — a new field, true only when a driver has both a
  `RequiredLibraryId` *and* a real staging provider/writer registered — mirrors `SupportsConnectionTest`'s
  own "hide an action that could never work" posture). A separate `ConnectionTestCard` section shows the
  last validation's own outcome (`useValidateLibrary`, a distinct mutation from `useTestConnection`) —
  succeeded/failed plus the CLI's own message, verbatim.
- **`ConnectionTestReport.LibraryWarning`** (new, nullable) — `ConnectionsController.Test`, on a
  *successful* test, additionally calls `DriverLibraryCompatibility.Check` and, if incompatible, sets:
  *"Connected, but the installed {AssemblyName} is missing N member(s) this driver uses — Validate
  library for a full check."* Surfaced in the SPA's `TestResult` as a warning-colored line beneath the
  connection result.

## Decisions made, and real bugs found

### Mono.Cecil, not AsmResolver

Both real, mature choices (open question 1). Chosen Cecil: `ModuleDefinition.GetMemberReferences()`/
`GetTypeReferences()` gives exactly the flat "every member this module's metadata tables reference into
another assembly" view this extraction needs in one call, with no need for AsmResolver's fuller (and
heavier) PE/managed-image object model — this phase has no other use for it.

### Matching by name + arity, not full signature — a deliberate, documented simplification

Comparing parameter *types* across two independently-loaded metadata contexts (Cecil's own type-reference
model for the driver's IL vs. `MetadataLoadContext`'s `Type` objects for the candidate) would need its own
cross-context type-identity comparison, for a narrower benefit than it costs: the real risk this phase
exists for is a member removed or renamed outright (`MissingMethodException`), which name+arity already
catches. A same-named, same-arity overload whose parameter types changed incompatibly is a real but
considerably rarer form of breaking change, and is out of scope here — recorded, not silently absorbed.

### The scratch schema and quoting — two real bugs, found only by running against a real Postgres container

The original implementation used cosmetic native-type strings (`"int"`/`"string"`/`"datetime"`) for the
`CachedColumn`s handed to the writer, and left table/schema identifiers unquoted. Both passed clean
against MsSql — and both broke, for real, the first time this ran against the real
`dbdatasync-postgres` container:

- **`42704: type "string" does not exist`** — `BatchInsertStagingProvider` (Postgres's own
  first-registered "native" staging provider; it has no bespoke one) reads `CachedColumn.NativeType`
  *literally* into its own staging-table DDL, unlike `MsSqlStagingTableProvider`, which re-discovers the
  real target table's column types live and ignores the passed-in value entirely. Fixed by giving
  `BuildTargetColumns` real, per-engine type spellings (`ScratchColumnTypes`, shared with
  `BuildCreateScratchTableSql` so the two can never drift apart again).
- **`relation "public.DbDataSync_LibraryValidation_..." does not exist"`** — an unquoted mixed-case scratch
  table name is silently folded to all-lowercase by Postgres's own unquoted-identifier rule at `CREATE
  TABLE` time, while `BatchInsertStagingProvider`/`DeleteInsertWriter` quote the *exact* case they were
  given when referencing it back — a mismatch. Fixed by quoting every identifier this file emits through
  the driver's own `SqlDialect.QuoteIdentifier` (`IDialectProvider`, which both `MsSqlDriver` and
  `PostgresDriver` implement) via a new `QuoteTable` helper.

Both are exactly the class of finding this phase's own "run it for real" testing bar exists to catch —
neither was visible from reading the code, only from actually running `validate` against a live Postgres
container.

### An unhandled crash on a genuinely broken/misconfigured library — found and fixed before it shipped

Manually reproducing a no-DDL-rights scenario surfaced a real gap: `driver.CreateConnection` and the
credential resolution ahead of it were not wrapped in a broad-enough catch. Against a scratch repo with no
library installed at all, or a connection whose secret can't be resolved, the child process crashed with
an unhandled `FileNotFoundException`/`SecretNotFoundException` instead of returning a clean `Failed`
result — precisely the "reported as a real, specific failure, not a generic crash" bar item 4 itself sets.
Fixed by widening the first `try`/`catch` in `LibraryValidationRunner.RunAsync` to also catch
`FileNotFoundException`/`FileLoadException`/`BadImageFormatException`/`TypeLoadException`/
`MissingMethodException`/`ClrKernel.Core.Secrets.SecretNotFoundException`, and by widening the staging/
write `catch` the same way (a member actually missing at runtime throws `MissingMethodException` from
inside `StageAsync`/`ApplyAsync`, not from `CreateConnection`).

### Stale-scratch-table sweeping: yes, best-effort

Open question 3, resolved yes: `validate` lists tables via the driver's own `ListTablesAsync` and drops
any matching `DbDataSync_LibraryValidation_*` before creating its own, on the reasoning that an orphan
from a genuinely crashed prior run (not a caught exception — those always reach the `finally`) is
otherwise invisible until someone notices it by hand. The sweep is itself best-effort: a failure to list
or drop doesn't fail the run in progress.

### `MetadataLoadContext`'s resolver setup — reused, not reinvented

`FactoryTypeReflector` (phase 122) had already solved "how do you set up `MetadataLoadContext` for a
restored library's own closure plus the runtime's BCL" — `LibrarySurfaceChecker` reuses the identical
`PathAssemblyResolver(libDir's DLLs + RuntimeEnvironment.GetRuntimeDirectory()'s DLLs)` pattern rather than
rediscovering it.

## What's explicitly out of scope / not built

- **A hard block on an incompatible library.** Every result here is `Warn`, never `Fail`, matching
  `library install`'s own existing posture.
- **Compatibility checking for descriptor-driven engines.** `RequiredLibraryId` stays null for all of
  them.
- **A build-time cached extraction artifact.** Computed at runtime, both directions, each time.
- **Validation against an operator's real tables/mappings.** The scratch table is a fixed, synthetic
  schema chosen for this check alone.
- **A separate `ILibraryCompatibilityProbe` interface.** Reuses the driver's real `IStagingProvider`/
  `IChangeWriter` directly.
- **Full signature-level (parameter-type) matching in the static check** — see "Decisions made" above;
  name+arity is the deliberate bar.
- **A genuinely incompatible real package version, exercised end to end.** Every older `Microsoft.Data.SqlClient`
  version actually tried during this phase (7.0.2, 3.0.0, 2.1.1) remained fully compatible with
  `MsSqlDriver`'s current usage — this driver's own real surface has been stable across that whole range,
  so no real "the pinned/older version is missing a member" scenario was found within reasonable effort.
  The detection mechanism itself was proven both directions for real regardless: the fixture unit tests
  (below) prove the positive/negative extraction+check mechanism with no real SqlClient/Npgsql involved,
  and a deliberate "point the real `MsSqlDriver`'s used surface at the real, installed `npgsql` library's
  `lib/` directory instead" sanity check reported all 28 real used members missing by name — a real,
  non-fabricated negative result. The CLI-level `library install`/`config check` *reporting* path
  (`PrintCompatibilityWarnings`/`LibraryCompatibilityCheck.Summarize`) is unit-tested directly against
  hand-built `DriverLibraryCompatibilityResult` values instead (see "How it was verified"), since it has
  no real incompatible built-in package to exercise it against.
- **Playwright coverage for the new "Validate library" button.** Deliberately not added — it needs a real
  child-process spawn against a real container from inside the Playwright harness (which already runs
  against real `dbdatasync-mssql-source`/`-target` containers, per 109i's own golden-path work), and the
  mechanism it would be proving is already covered, more directly and more repeatably, by
  `DbDataSync.Api.Tests`' own `LibraryValidateIntegrationTests` (the real HTTP endpoint, the real spawned
  child process, the real container, a real no-DDL-rights failure, and the isolation acceptance test) and
  `DbDataSync.Cli.Tests`' `LibraryValidateCommandTests`. Flagged explicitly, not silently skipped — matching
  this repo's own established convention for a deliberately-scoped gap.

## How it was verified

- **Unit test with fixture assemblies** (`tests/DbDataSync.Libraries.Tests/LibraryCompatibilityTests.cs`,
  against `tests/fixtures/DbDataSync.Libraries.CompatFixtureDriver` and its two
  `CompatFixtureLibrary{Complete,Incomplete}` siblings, same "publish on the fly, not part of
  `DbDataSync.slnx`" shape as the existing `FactoryFixture*` fixtures): the extraction step finds every
  member the fixture driver actually references (a default constructor, a field, and two overloads of the
  same method name at different arities), passes clean against the complete library version, and against
  the incomplete version (missing the one-argument overload) names exactly that member as missing —
  without naming the members that are still present. 3/3 passed.
- **Real-world confirmation against the real, currently-pinned libraries and real driver DLLs**:
  `microsoft-data-sqlclient` 7.0.2 vs. the real `MsSqlDriver.dll` (28 used members, 0 missing), `npgsql`
  9.0.3 vs. the real `PostgresDriver.dll` (14 used members, 0 missing), `duckdb` 1.5.5 vs. the real
  `DuckDbDriver.dll` (4 used members, 0 missing) — all clean, no false positives against real, current
  usage. Also tried `microsoft-data-sqlclient` 3.0.0 and 2.1.1 against the same real driver DLL: both
  remained clean (this driver's real surface has been stable across that whole range — see "What's
  explicitly out of scope" above for why no genuinely incompatible version was found).
- **A real, non-fabricated negative-path sanity check**: pointed the real `MsSqlDriver.dll`'s extracted
  surface at the real, installed `npgsql` library's `lib/` directory instead of `microsoft-data-sqlclient`'s
  own — all 28 used members correctly reported missing, each by exact name (`SqlConnection`,
  `SqlBulkCopy.WriteToServerAsync(2 arg(s))`, etc.), proving the checker fails for real against a real
  (if mismatched) installed library directory, not merely by construction.
- **`config check`'s new "Library compatibility" line**: `ReadinessChecksTests.cs` —
  `NoLibrariesInstalled_LibraryCompatibilityCheckIsSilentlyOk` (nothing installed → `Ok`, silent) and
  `EveryBuiltInLibraryInstalledAtItsPinnedVersion_LibraryCompatibilityCheckIsClean` (all three real
  libraries installed at their pinned versions, via a real `LibraryInstaller.InstallAsync` restore → `Ok`).
  The per-library dedup/formatting logic (`LibraryCompatibilityCheck.Summarize`) is unit-tested directly
  against hand-built results proving two drivers sharing one incompatible library are reported once,
  naming both.
- **`dbdatasync config library validate`, run standalone against the real containers already used
  throughout this repo** (`docker ps`: `dbdatasync-mssql-source`/`-target`, `dbdatasync-postgres`):
  - MsSql (port 14330, `sa`): succeeded — *"staged and wrote 3 row(s) via MsSqlStagingTable/MsSqlMerge.
    Scratch table cleaned up."* — confirmed via `sys.tables` afterward: 0 matching rows.
  - Postgres (port 15432, `dbdatasync`): succeeded — *"staged and wrote 3 row(s) via
    StagingTable/DeleteInsert. Scratch table cleaned up."* — confirmed via `pg_tables` afterward: 0
    matching rows. (This is the run that found and fixed both real bugs above.)
  - DuckDb: a clean, specific *"has no staging provider/writer registered — there is nothing to stage or
    write end to end for this engine"* failure, not a crash or an `IndexOutOfRangeException`.
  - Usage/argument errors (`validate` with no args, no `--connection`, an unknown connection, a library id
    that doesn't match the connection's driver): each a clean exit code 1 with a specific message.
  - **The isolation acceptance test, done for real, not assumed**: a throwaway harness (the same
    "throwaway console app" methodology 109h's own retrospective used) constructed a real
    `LibraryValidationLauncher` pointed at the real, built `DbDataSync.Cli.dll` and the real MsSql
    container, and checked `AppDomain.CurrentDomain.GetAssemblies()` for `Microsoft.Data.SqlClient`
    before and after: **0 both times**, while the spawned child process itself reported success
    ("staged and wrote 3 row(s)..."). This is the actual proof the isolation this phase's child-process
    design exists for is real, not merely a design intention.
  - **No-DDL-rights failure, against a real restricted SQL login**
    (`tests/DbDataSync.Api.Tests/LibraryValidateIntegrationTests.cs`,
    `Validate_WithNoDdlRights_FailsNamingTheCreateTableStep_NotAGenericStagingError`): a real `CREATE
    LOGIN`/`CREATE USER` with only `db_datareader` on `master`, real credential exposed to the spawned
    child via the exact environment variable `GET .../credential-source` reports (see below) — fails
    naming CREATE TABLE and the scratch table specifically, not a generic staging error. A real,
    reproducible SQL Server quirk found in the process: `DROP LOGIN IF EXISTS` is not valid syntax (unlike
    `DROP USER IF EXISTS`) — worked around in test cleanup with an `IF EXISTS (SELECT ...) DROP LOGIN`
    guard.
  - **A pinned-older-version negative run was not separately re-run against `validate` end to end** — the
    static check (above) already establishes no real incompatible version exists to try; re-running the
    deep check against one would need one that doesn't exist. Flagged explicitly per this phase's own
    "how to verify" bar, rather than silently skipped.
- **The API endpoint, exercised for real** — three tests in `LibraryValidateIntegrationTests.cs`, all
  against the real `dbdatasync-mssql-source` container through the real `TestApiFactory` HTTP client
  (which required teaching `TestApiFactory` to resolve `DbDataSync:CliDllPath` for tests, since its own
  default resolution assumes `DbDataSync.Api` is the entry assembly — the identical existing pattern
  `TaskRunnerDllPath` already needed, generalized into `ResolveSiblingDllPathForTests`), plus the exact
  cross-process credential-visibility handling `LibraryValidationLauncher`'s own doc comment describes
  (the test host's `SecretStore` is swapped for an in-memory one, invisible to the spawned child process,
  so the test sets the real environment variable `GET .../credential-source` reports before triggering
  validate — the child inherits it, the same as any spawned child inherits its parent's environment):
  a successful validate + scratch table gone, a no-DDL-rights failure, and confirmation that
  `POST .../test` reports no `LibraryWarning` against the currently-pinned, compatible library.
- **CLI-layer tests** (`tests/DbDataSync.Cli.Tests/LibraryValidateCommandTests.cs`, 7 tests): usage/argument
  errors with no real DB needed, plus two `Category=Integration` tests running `LibraryCommand.RunAsync`
  directly against the real MsSql and Postgres containers (the second is what would have caught the two
  Postgres bugs above, and now guards against a regression of either).
- **SPA**: `npm run build` (tsc + vite) clean; `npm run lint` (oxlint) clean — the pre-existing
  `set-state-in-effect`/`exhaustive-deps` warnings elsewhere in the SPA are unrelated to this phase's own
  changes (confirmed by diffing against `HEAD`, not merely by line number). No Playwright coverage added
  for the new button — see "What's explicitly out of scope" above.
- Full test suites for every touched assembly, confirming no regressions:
  - `DbDataSync.Libraries.Tests`: 8 passed (5 pre-existing + 3 new).
  - `DbDataSync.Drivers.Abstractions.Tests`: 68 passed, unaffected by the new `IDriver.RequiredLibraryId`
    default member or `DriverCapabilities.SupportsLibraryValidation`.
  - `DbDataSync.Drivers.Generic.Tests`: 184 passed, unaffected.
  - `DbDataSync.Drivers.MsSql.Tests`: 137 passed non-integration, 124 passed `Category=Integration`.
  - `DbDataSync.Drivers.Postgres.Tests`: 36 passed non-integration, 28 passed `Category=Integration`.
  - `DbDataSync.Drivers.DuckDb.Tests`: 33 passed (no `Category=Integration` suite exists here, per 109i's
    own finding, still true).
  - `DbDataSync.Cli.Tests`: 117 passed non-integration (110 baseline + 7 new usage-error/duckdb tests +
    2 new `Summarize`/config-check tests) — 8 passed `Category=Integration` (5 pre-existing + 3 new
    `LibraryValidateCommandTests`, 2 of which are real MsSql/Postgres round trips).
  - `DbDataSync.TaskRunner.Tests`: 45 passed non-integration, 32 passed `Category=Integration` —
    unaffected; this phase touches nothing in `DbDataSync.TaskRunner` itself.
  - `DbDataSync.State.Tests`: 187 passed non-integration, unaffected.
  - `DbDataSync.Api.Tests`: 422 passed non-integration, 23 skipped (Windows-only) — byte-identical to
    109h/109i's own baseline; `Category=Integration` **69 passed, 0 failed** (includes the 3 new
    `LibraryValidateIntegrationTests` alongside the full pre-existing integration suite — the same 69
    count 109h's own retrospective established as its baseline). One real flake along the way, not a
    regression: a first full run hit `ConcurrentRunsIntegrationTests` failing with `Microsoft.Data.SqlClient`
    unloadable, caused by resource contention from running the entire ~69-method suite (many real
    container connections, many spawned `TaskRunner` child processes) at once in this sandbox — it passed
    cleanly both standalone and on a full clean re-run immediately after, and this phase touches nothing
    in `ConcurrentRunsIntegrationTests` or the library auto-install path it exercises.
