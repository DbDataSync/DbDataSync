# Phase 109h — built-in replication drivers off the provider packages

**Status**: Complete.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*What still can't be
decoupled, and why*. Depended on 109c (the provider/library layer) — specifically its `LibraryRegistry`
resolver, confirmed already built and already the right shape for this, not something this phase added.

## The problem, unchanged

`DbDataSync.State` needed its providers in one line each (109g). The **replication** drivers use the
typed provider API meaningfully — `SqlBulkCopy`, typed `SqlDbType`/`NpgsqlDbType` segment-bound
binding, `SqlException.Number` — which this doc's original table already laid out and is still
accurate:

| driver | typed provider use | has a `System.Data.Common` form? |
| --- | --- | --- |
| `MsSqlDriver` / `MsSqlStagingTableProvider` | `SqlBulkCopy` (×8) | **no** — `BatchInsertStagingProvider` (multi-row INSERT) is the only portable staging |
| `MsSqlSegmentScope` | `SqlDbType` (×28) — typed segment-bound binding | partial — `System.Data.DbType` covers most, loses `Decimal` precision/scale nuance |
| `MsSqlChangeTrackingReader` | `SqlException.Number == 3952` | `DbException` has `SqlState` / provider-specific reflection |
| `MsSqlDriver` | `SqlConnectionStringBuilder` (×6) | yes — `DbConnectionStringBuilder` base + string keys |
| `PostgresDriver` / `PostgresValueBinding` | `NpgsqlDbType` (×17), `NpgsqlConnectionStringBuilder` | partial / yes |

**What's different now: this table stops mattering.** Both options this doc originally posed either
accepted the compiled reference (Option A) or paid to abstract or relocate the typed usage (Option B).
The chosen design needs none of that — the driver code keeps using `SqlBulkCopy`/`SqlDbType`/
`NpgsqlDbType` exactly as written, because it's still compiling against the real package. The only
thing that changes is whether the assembly ships embedded.

## The decision: `ExcludeAssets="runtime"`

MSBuild's `PackageReference` asset buckets separate `compile` from `runtime`. Excluding only `runtime`
keeps the reference assembly available to the compiler — every `SqlBulkCopy`/`SqlDbType` call site
keeps compiling unchanged — while the actual DLL is neither copied into the driver project's own
output nor listed in the hosting project's (`DbDataSync.Api`/`DbDataSync.Cli`) `.deps.json`.

At execution, `MsSqlDriver.dll`'s IL still references `Microsoft.Data.SqlClient` by assembly name.
With it absent from local probing, the first touch raises `AssemblyLoadContext.Default`'s `Resolving`
event — which `LibraryRegistry` **already arms**, confirmed by reading it rather than assumed:

```csharp
// src/DbDataSync.Libraries/LibraryRegistry.cs:170
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (var resolver in Resolvers)
    {
        var path = resolver.ResolveAssemblyToPath(name);
        if (path is not null)
            return context.LoadFromAssemblyPath(path);
    }
    return null;
};
```

This is the identical mechanism 109c/109d/109e already built for the descriptor and compiled-plugin
paths — nothing new was needed in `DbDataSync.Libraries` itself. What was new was aiming a driver that
stays compiled directly into the main solution at it, rather than only a loaded-separately plugin.

**Why this beats both original options**, not just "is cheaper than Option B":

- Against **Option A** (keep hard-referenced, accept the patch-build cost): gets the actual goal — a
  provider CVE fix becomes `library sync`, not a DbDataSync rebuild — for the compiled drivers too, not
  only the state store and descriptor drivers 109c/109g/109d already cover. Proved for real during
  verification (see below), not just argued for.
- Against **Option B** (move to first-party plugins): no `Abstractions`-as-a-package boundary, no
  per-driver `AssemblyLoadContext`, no change to `DbDataSyncHost`'s composition root, no `SqlDbType` →
  `DbType` precision loss to weigh — `MsSqlDriver`/`PostgresDriver` stay ordinary project references,
  fully debuggable, exactly where they are.

## What this built

### 1. `MsSqlDriver.csproj` / `PostgresDriver.csproj`

```xml
<PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" ExcludeAssets="runtime" />
```

and the equivalent for `Npgsql` in `DbDataSync.Drivers.Postgres.csproj`. No `.cs` file in either
project changed.

### 2. Test projects needing their own direct reference — bigger than the doc guessed

The original plan checked (rather than assumed) that `tests/DbDataSync.Drivers.MsSql.Tests.csproj` and
`tests/DbDataSync.Drivers.Postgres.Tests.csproj` had no *direct* `PackageReference` to
`Microsoft.Data.SqlClient`/`Npgsql`, only a transitive one through their `ProjectReference` to the
driver — and that excluding `runtime` assets on the driver's own reference would remove that transitive
copy too. Both got their own direct, **not** excluded, `PackageReference` to the identical package and
version the driver pins:

- `DbDataSync.Drivers.MsSql.Tests.csproj` → `Microsoft.Data.SqlClient` `7.0.2`.
- `DbDataSync.Drivers.Postgres.Tests.csproj` → **both** `Microsoft.Data.SqlClient` `7.0.2` **and**
  `Npgsql` `9.0.3` — the plan's "one package per test project" framing missed that this project also
  `ProjectReference`s the MsSql driver and touches `Microsoft.Data.SqlClient` directly
  (`MsSqlScratchDatabase.cs`, `CrossEngineReplicationTests.cs`) for its own cross-engine scratch-database
  setup, which the same `ExcludeAssets` change also stripped transitively.

**A real, larger-than-planned gap found by actually running the full suite, not by re-reading the
plan.** The two driver test projects were not the only casualties: `Microsoft.Data.SqlClient` is used
*directly* (not just through the driver under test) by test scaffolding in three more projects, each of
which had been getting it purely transitively through a `DbDataSync.Drivers.MsSql` `ProjectReference` —
`DbDataSync.Cli.Tests` (`InviteCommandTests.cs`, a real `CREATE DATABASE` in its constructor before the
109g-era library install even runs), `DbDataSync.TaskRunner.Tests` (`RunExecutorIntegrationTests.cs`,
`Scd2NaturalKeyIntegrationTests.cs`), and `DbDataSync.Api.Tests` (a dozen integration test files' own
scratch-database setup). All three got the identical treatment: their own direct, un-excluded
`Microsoft.Data.SqlClient 7.0.2` `PackageReference`. `DbDataSync.State.Tests` and
`DbDataSync.Drivers.Generic.Tests` already carried the direct references they needed from earlier
phases (109g and an unrelated Npgsql use respectively) and needed no change. Found by running
`InviteCommandTests` (a `FileNotFoundException` in its own constructor, before any of this phase's
production code even ran) after everything else already looked green — a reminder that "the two test
projects the plan named" and "every test project that touches the type" are different questions, and
only running the actual suites answers the second one.

### 3. Auto-seed `microsoft-data-sqlclient` and `npgsql` when they're actually chosen

Two separate seams, resolved differently — see "Decisions made" below for why they ended up different
shapes rather than one mechanism copied twice.

- **`Tui/SetupSteps.ApplyStateDatabaseAsync`** (was `ApplyStateDatabase`, now `async`): when an operator
  picks `MsSql`/`Postgres` as `StateEngine`, installs the matching library first if it isn't already —
  exactly `InstallMySqlDriverAsync`'s shape, reusing the identical `installLibrary` delegate
  (`Func<string,string,IReadOnlyList<PackageRef>,string,string?,CancellationToken,Task<LibraryManifest>>`)
  already threaded from `SetupCommand.RunAsync` → production: `LibraryInstaller.InstallAsync` →
  `SetupScreen.RunAsync` → the tab's own save call. `StateDatabaseTab.Save(string root)` became
  `SaveAsync(string root, installLibrary)` — the exact signature change 109g's retrospective predicted
  and deliberately left undone, now made. `SetupScreen.cs`'s one call site (`stateDatabase.Save(root)`)
  became `await stateDatabase.SaveAsync(root, installLibrary)`.
- **The connection-creation path — resolved as `DriverConnectionFactory.OpenAsync`, not
  `ConnectionsController.Upsert`.** The phase doc explicitly left this an open question, naming both as
  candidates. See "Decisions made" below for the reasoning and the real cost data that decided it.

### 4. Cross-reference: DuckDB gets the *unconditional* version of this in 109i

Unchanged from the plan — 109i is a separate phase, not started here, and not touched by this one.

## What this does not do

- **Does not move `MsSqlDriver`/`PostgresDriver` out of the main solution.** They stay ordinary project
  references — see "Why this beats both original options," above.
- **Does not touch `SqlDbType`/`NpgsqlDbType`/`SqlBulkCopy` usage anywhere.** Confirmed by the diff: the
  only `.cs` changes in either driver project are zero — every change is in `.csproj` files, in
  `DbDataSync.Cli`'s setup wizard, and in `DbDataSync.Api`'s `DriverConnectionFactory`.
- **`DuckDbDriver` and DuckDB decoupling generally** — unchanged, still 109i's own separate concern.
- **`DbDataSync.TaskRunner`'s own connection-opening path gets no new auto-install hook.** See
  "What's explicitly out of scope" below — a real, deliberate gap, not an oversight.

## Decisions made, and real bugs found

### The connection-creation seam: `DriverConnectionFactory.OpenAsync`, not `ConnectionsController.Upsert`

The phase doc named both as candidates and left the choice for implementation time. `Upsert` was tried
first and reverted after measuring the actual cost:

- **Saving a connection's config never touches the driver's typed provider types at all.** Nothing about
  persisting `ConnectionConfig` to YAML needs `Microsoft.Data.SqlClient`/`Npgsql` to be loadable —
  nothing breaks until something actually *opens* the connection. Hooking `Upsert` would have installed
  a library the moment an operator (or a test) merely saved connection metadata, whether or not anything
  ever used it.
- **Concretely measured, not assumed:** `ConnectionsControllerTests` alone has half a dozen tests that
  `PUT` a `DriverType: MsSql` connection and never open it (`Capabilities_...`, `Upsert_ThenGet_RoundTrips`,
  `Upsert_NeverReturnsOrCommitsPlaintextPassword`, `List_IncludesUpsertedConnection`,
  `Delete_ThenGet_Returns404`) — and dozens more across the suite follow the same `MakeInput`-with-MsSql
  pattern purely for unrelated fixture setup. Each of those tests uses its own `TestApiFactory` (a fresh
  temp repo root, `IClassFixture`-scoped per test class), so hooking `Upsert` would have charged one real
  `dotnet publish`-backed install to the *first* `PUT` in every one of those classes — pure overhead for
  tests that have nothing to do with connectivity.
- **`DriverConnectionFactory.OpenAsync` is the one seam every real use of a connection already goes
  through** — the Test button (`ConnectionsController.Test`), schema browsing (`MetadataService`),
  backfill segment-bound computation (`BackfillService`), and every provisioning check. Hooking there
  means the auto-install cost (and its coverage) lands exactly on what actually needs the assembly
  loadable, and nowhere else. It is also, by construction, exercised by every existing MsSql/Postgres
  integration test in the solution — not asserted once and hoped to generalize, but proven the moment
  those pre-existing tests (`ConnectionTestIntegrationTests`, `ConnectionStringAddressingTests`,
  `BackfillIntegrationTests`, and a dozen more) ran green against fresh, library-free repo roots.
- `EnsureLibraryInstalledAsync` (private to `DriverConnectionFactory`) checks
  `libraryRegistry.Installed`, installs via `LibraryInstaller.InstallOrDeferAsync` at the
  `KnownLibraries` pinned version if missing, then `RegisterInstalled` arms this process's resolver
  immediately (no restart — the same "no restart needed" idiom `POST /api/libraries` already uses). A
  `PendingRestore` outcome (no SDK, no in-image cache hit) throws an `InvalidOperationException` naming
  `config library sync` as the fix, which `ConnectionsController.Test`'s existing catch clause already
  turns into a `succeeded: false` report rather than a 500 — no change needed there.

### `SetupSteps` tests: real installs, not fakes — matching this repo's own precedent

`SetupStepsTests`/`TabWiringTests`' MsSql-flow tests now use `LibraryInstaller.InstallAsync` for real
(the same delegate `SetupCommand.RunAsync` passes in production), not a fake. A fake that only writes a
manifest without a real `lib/` directory would have made the *later* `StateDatabase.FromOptions` call
reach `DbProviderFactories.GetFactory` — which needs a real, loadable assembly — instead of short-
circuiting on "not installed" the way it did before this phase (109g's own retrospective already
documents that short-circuit). `DbDataSync.State.Tests`' `LibraryInstallFixture` already established real
installs as this repo's answer to exactly this shape of problem; `SetupStepsTests`/`TabWiringTests`
followed it rather than inventing a fake with different (and misleading) failure characteristics. Real
installs from the local NuGet cache turned out fast in practice — the affected tests added well under
ten seconds to the whole `DbDataSync.Cli.Tests` run.

### The `ExcludeAssets` blast radius was bigger than "the two driver test projects" — three more fixed

Covered under "What this built" §2 above; repeated here because it's the most consequential finding of
the phase. `DbDataSync.Cli.Tests`, `DbDataSync.TaskRunner.Tests`, and `DbDataSync.Api.Tests` all needed
their own direct `Microsoft.Data.SqlClient` `PackageReference` because their own test scaffolding —
not the driver under test — used the type directly. Every one of these was previously invisible because
the driver project's *embedded* copy quietly supplied it. This is exactly the kind of gap `ExcludeAssets`
was designed to surface (a hosting project's `.deps.json` should not carry a package it never asked
for) — it surfaced it in test projects first, which is the reason to run the full suite rather than
trust that "compiles clean" is the same thing as "the acceptance test passes."

### Host startup does not require the assembly to be loadable — verified, not assumed

A real risk this phase had to rule out before trusting anything else: `DbDataSyncHost.Build()` and
`DbDataSync.TaskRunner`'s `Program.cs` both unconditionally construct `new MsSqlDriver()` and
`new PostgresDriver()` at startup, regardless of whether an operator ever uses either engine. If
constructing those types forced the CLR to load `Microsoft.Data.SqlClient`/`Npgsql` eagerly, every
deployment with neither library installed would fail to start the moment this phase shipped — not a
theoretical worry, since before this phase the assemblies were always physically present and this path
was never exercised. Verified with a small throwaway console app (published with the same
`ExcludeAssets`-affected `ProjectReference`s, run with no `Microsoft.Data.SqlClient`/`Npgsql` DLL
anywhere in its own output) that constructs both driver types and prints success with no exception —
confirmed the CLR's per-method (not per-class) JIT-time assembly loading means neither constructor's
field initializers touch a provider type eagerly. This is *why* items 1–2 above are safe and item 3 only
needs to trigger on an actual connection open, not on host startup.

## What's explicitly out of scope / not built

- **`DbDataSync.TaskRunner`'s own connection-opening path is not given a new auto-install hook.**
  `TaskRunner.Program.cs` already calls `new LibraryRegistry(repoRoot).LoadAll()` at spawn (unchanged,
  pre-existing), which correctly arms its own process's resolver from whatever is already installed on
  shared disk. In the normal flow (create a connection → test it, or run backfill/provisioning against
  it, all through the API's `DriverConnectionFactory` → build a mapping → schedule a run) the library is
  already on disk by the time a `TaskRunner` worker spawns for that connection, so this needs nothing
  further. If an operator somehow schedules a real replication run against a connection that was never
  opened once through the API first, the worker process would still fail with an unloadable-assembly
  error — a real, narrow gap, left open deliberately rather than adding a second install call site for a
  path this phase's own testing found no realistic way to reach in the existing setup-then-run UX.
- **A version-bump check was fully exercised, not skipped or merely asserted possible** — see
  "How it was verified" below. Practical to test in this sandbox (real network access to nuget.org, a
  real SQL Server container) and it was.
- Everything the original doc already scoped out is still out: no move to first-party plugins, no
  `SqlDbType`/`NpgsqlDbType`/`SqlBulkCopy` rewrite, no DuckDB work.

## How it was verified

- `dotnet build DbDataSync.slnx` — clean, 0 warnings/errors, confirming `ExcludeAssets="runtime"`
  disturbs nothing at compile time.
- `dotnet publish src/DbDataSync.Api` — the publish output directory contains
  `DbDataSync.Drivers.MsSql.dll`/`DbDataSync.Drivers.Postgres.dll` but **no**
  `Microsoft.Data.SqlClient.dll`/`Npgsql.dll` anywhere. Cross-checked against `dotnet build`'s own
  `bin/` output and its `.deps.json` (the `Microsoft.Data.SqlClient` package entry has no `runtime`
  target — no DLL to copy — confirming the exclusion is real, not merely absent from this one directory
  listing by chance).
- `DbDataSync.Drivers.MsSql.Tests` / `.Drivers.Postgres.Tests`, each run **standalone**
  (`dotnet test` on just that project): both non-`Integration` (137, 36 passed) and `Category=Integration`
  against the real `dbdatasync-mssql-source`/`dbdatasync-postgres` containers (124, 28 passed) — proving
  each project's own new direct reference actually restores what the transitive one used to provide, for
  real connections, not just for compilation.
- **Fresh-deployment check, done for real against a live container, not simulated:** a throwaway
  console app (referencing the driver projects exactly as `DbDataSync.Api`/`.Cli` do, so it inherits the
  same `ExcludeAssets` behaviour) published to a directory with no `Microsoft.Data.SqlClient.dll`
  anywhere, pointed at a scratch repo root with `dbdatasync config library install
  microsoft-data-sqlclient --version 7.0.2` already run (simulating the auto-seed's own end state, since
  the throwaway app has no controller to trigger the seed itself) — `MsSqlDriver.CreateConnection(...)`
  followed by `OpenAsync()` against the real `dbdatasync-mssql-source` container succeeded, and
  `connection.GetType().Assembly.Location` confirmed the type actually loaded from
  `<scratch>/libraries/microsoft-data-sqlclient/lib/Microsoft.Data.SqlClient.dll` — not from anywhere
  the driver project itself shipped, because it shipped nothing.
- **The real auto-seed path, exercised by the automated suite itself, not just the standalone probe:**
  `ConnectionTestIntegrationTests` and `ConnectionStringAddressingTests` (existing tests, unmodified)
  each run against a brand-new `TestApiFactory` temp repo root with no `libraries/` directory — creating
  an MsSql or Postgres connection via `PUT /api/connections/{name}` and then calling
  `POST /api/connections/{name}/test` succeeds end to end, including the Postgres case
  (`Postgres_TakesAConnectionStringToo`), with no library ever manually installed. This is exactly the
  acceptance test the phase doc asked for, and it passes without any change to those tests' own code —
  the auto-seed in `DriverConnectionFactory` is what made them keep passing after `ExcludeAssets` shipped.
- **Version-bump check, fully exercised (this sandbox has real network access to nuget.org and a real
  SQL Server container, so this was not skipped):** installed `microsoft-data-sqlclient` at the pinned
  `7.0.2` into a scratch repo, ran the same already-built throwaway probe (no rebuild) — connected, file
  version `7.0.2.26175`. Reinstalled the *same* library id at `7.0.3` (a real, newer, published version —
  simulating a patch/CVE-fix bump) via `dbdatasync config library install microsoft-data-sqlclient
  --version 7.0.3`, then ran the **identical, still-unrebuilt** probe binary again — connected again, file
  version now `7.0.3.26253`. No DbDataSync source file, project, or binary was touched between the two
  runs; only the installed library on disk changed. This is the exact acceptance bar the original doc set
  for Option B ("a SqlClient bump becomes `dbdatasync driver sync`, not a rebuild"), now met by
  `ExcludeAssets` alone.
- Full test suites for every touched assembly, confirming no regressions:
  - `DbDataSync.Cli.Tests`: 110 passed (was 109 before this phase; `SetupStepsTests` gained one new test
    for the "already installed, do not reinstall" idempotency case).
  - `DbDataSync.Api.Tests` non-integration: 422 passed, 23 skipped (Windows-only) — byte-identical to
    109g's own baseline.
  - `DbDataSync.Api.Tests`, targeted `Category=Integration` runs covering every Integration-tagged test
    file that touches `Microsoft.Data.SqlClient`/`Npgsql` directly or exercises a real MsSql/Postgres
    connection open (`BackfillIntegrationTests`, `ChangeCounterSourceIntegrationTests`,
    `DescriptorDriverTests`, `ApplyProvisioningCacheIntegrationTests`, `PreviewIntegrationTests`,
    `MetadataControllerIntegrationTests`, `ReconcileDeletesIntegrationTests`,
    `ReconcileDeletesScd2IntegrationTests`, `CrossInstanceEndToEndTests`, `BulkCreateRunIntegrationTests`,
    `ConcurrentRunsIntegrationTests`, `ConnectionTestIntegrationTests`, `ConnectionStringAddressingTests`,
    plus the Libraries/Drivers admin suites `LibrariesControllerTests`, `LibrarySearchTests`,
    `LibraryInstallTests`, `DriversControllerTests` for DI-wiring safety since `DriverConnectionFactory`'s
    constructor gained two new dependencies): every one green, ~69 test methods across four separate runs.
  - `DbDataSync.TaskRunner.Tests`: full suite, 77 passed (its own new direct `Microsoft.Data.SqlClient`
    reference exercised by `RunExecutorIntegrationTests`/`Scd2NaturalKeyIntegrationTests`, both
    `Category=Integration`, against real containers).
  - `DbDataSync.State.Tests` non-integration: 187 passed — untouched by this phase, confirmed unaffected.
  - `DbDataSync.Drivers.Generic.Tests` non-integration: 184 passed; `DbDataSync.Libraries.Tests`
    non-integration: 5 passed — both untouched, confirmed unaffected.
