# Phase 109h — built-in replication drivers off the provider packages (planned)

**Status**: Planned, not started. **Decided 2026-09-13**: compile against the provider, don't embed
it — `ExcludeAssets="runtime"` on the driver projects' own `PackageReference`, not a rewrite into
first-party plugins. This supersedes the two options this doc previously left undecided; both are kept
below, with the reasoning that ruled them out, per this repo's own planning convention of recording a
path not taken rather than deleting it.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*What still can't be
decoupled, and why*. Depends on 109c (the provider/library layer) — specifically its `LibraryRegistry`
resolver, confirmed already built and already the right shape for this, not something this phase adds.

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
paths — nothing new to build in `DbDataSync.Libraries` itself. What's new is aiming a driver that stays
compiled directly into the main solution at it, rather than only a loaded-separately plugin.

**Why this beats both original options**, not just "is cheaper than Option B":

- Against **Option A** (keep hard-referenced, accept the patch-build cost): gets the actual goal — a
  provider CVE fix becomes `library sync`, not a DbDataSync rebuild — for the compiled drivers too, not
  only the state store and descriptor drivers 109c/109g/109d already cover.
- Against **Option B** (move to first-party plugins): no `Abstractions`-as-a-package boundary, no
  per-driver `AssemblyLoadContext`, no change to `DbDataSyncHost`'s composition root, no `SqlDbType` →
  `DbType` precision loss to weigh — `MsSqlDriver`/`PostgresDriver` stay ordinary project references,
  fully debuggable, exactly where they are.

## What this builds

### 1. `MsSqlDriver.csproj` / `PostgresDriver.csproj`

```xml
<PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" ExcludeAssets="runtime" />
```

and the equivalent for `Npgsql` in `DbDataSync.Drivers.Postgres.csproj`. No `.cs` file in either
project changes.

### 2. The test projects need their own direct reference

Checked, not assumed: `tests/DbDataSync.Drivers.MsSql.Tests.csproj` and
`tests/DbDataSync.Drivers.Postgres.Tests.csproj` have **no direct `PackageReference`** to
`Microsoft.Data.SqlClient`/`Npgsql` today — both get the package purely transitively, through their
`ProjectReference` to the driver. Excluding `runtime` assets on the driver's own reference removes that
transitive copy too, which would break a standalone `dotnet test` run the moment it touches a
`SqlClient`/`Npgsql` type. Fix: add each test project its own, **not** excluded, `PackageReference` to
the identical package and version the driver project pins — harmless duplication, and the test project
keeps a real local copy for exactly the same reason the test host doesn't otherwise bootstrap
`LibraryRegistry`'s resolver.

### 3. Auto-seed `microsoft-data-sqlclient` and `npgsql` when they're actually chosen

Both are already `KnownLibraries` catalog entries, and the container image already restores every
`KnownLibraries` entry into a build-time cache (`internal build-catalog-cache`, phase 121) that
`library install`/`sync` then copies from with no SDK and no network. What's missing: today that
install still needs an explicit `dbdatasync config library install microsoft-data-sqlclient`/`npgsql`
(or `sync`) command run at least once. MSSQL is DbDataSync's original v1 engine and Postgres has been
a first-class one since phase 20 — neither can start requiring a manual library-install step just
because this phase stopped embedding their assemblies.

**Not guessed at — 109g's own retrospective already named the exact template and the exact two seams
it deliberately left for whoever built this.** `Tui/SetupSteps.InstallMySqlDriverAsync`
(`src/DbDataSync.Cli/Tui/SetupSteps.cs:128`) already proves the shape: an async install through the
`installLibrary` delegate (`Func<string,string,IReadOnlyList<PackageRef>,string,string?,
CancellationToken,Task<LibraryManifest>>`) already threaded from `SetupCommand.RunAsync` → production:
`LibraryInstaller.InstallAsync` → `SetupScreen.RunAsync` → a tab's own `SaveAsync`. This phase applies
that same shape to the two places 109g's retrospective named and explicitly declined to touch within
its own scope:

- **`Tui/SetupSteps.ApplyStateDatabase`** — when an operator picks `MsSql`/`Postgres` as the
  `StateEngine`, install the matching library first, the same way `InstallMySqlDriverAsync` installs
  `MySqlConnector` before writing `drivers/mysql/driver.yaml` (minus that driver-manifest tail — these
  two are already built-in, nothing to seed beyond the library itself). 109g's own words on why this
  wasn't done there: *"`StateDatabaseTab.Save(string root)` is synchronous today, and threading an
  async install through it means the same signature change `DriversTab`/`SetupScreen` already absorbed
  once... a real, separable UX decision."* That decision is this phase's to make.
- **The connection-creation path** — when an operator configures a connection using the `MsSql`/
  `Postgres` driver type, the same install-if-missing check applies. The exact current hook (the SPA's
  connection-save flow, or `DriverConnectionFactory`/`ConnectionsController` on the API side) needs
  confirming against the real code at implementation time — not assumed here the way the state-database
  seam already was.

### 4. Cross-reference: DuckDB gets the *unconditional* version of this in 109i

Unlike these two — needed only if an operator chooses them — DuckDB is required by every deployment
regardless of configuration (verification and segmenting always use it). 109i specs that as its own,
different trigger (install once, unconditionally, on every `serve` start rather than on a choice) —
see `architecture/implementation/todo/phase-109i-duckdb-decoupling.md`. Built once each because the
triggers genuinely differ, not duplicated by accident.

## What this does not do

- **Does not move `MsSqlDriver`/`PostgresDriver` out of the main solution.** They stay ordinary project
  references — see "Why this beats both original options," above.
- **Does not touch `SqlDbType`/`NpgsqlDbType`/`SqlBulkCopy` usage anywhere.** The whole point: nothing
  about the typed-provider-API table above needs solving, because the code never stops compiling
  against the real types.
- **`DuckDbDriver` and DuckDB decoupling generally** — unchanged, still 109i's own separate concern
  (verification and scripting also embed `DuckDB.NET`, a wider surface than this phase touches).

## Considered and ruled out — kept for the record

### Option A — keep them compiled and hard-referenced

- `MsSqlDriver` etc. keep an ordinary `<PackageReference>`, embedded as today.
- A `Microsoft.Data.SqlClient` advisory means a DbDataSync patch build for the compiled fast paths —
  the state store (109g) and descriptor drivers (109d) already don't have this problem.
- Ruled out because the chosen design gets the same "stays compiled, no code changes" property *and*
  closes this gap, for the same implementation cost as adding one MSBuild attribute per project.

### Option B — first-party plugins

- `MsSqlDriver`/`PostgresDriver`/`DuckDbDriver` move to `<repo>/drivers/mssql/` etc., loaded through
  109e's compiled-plugin loader, each carrying its own provider in `lib/`.
- A SqlClient bump becomes `dbdatasync driver sync` — the complete version of the original goal, at
  real cost: `Abstractions` as a versioned public contract for these three specifically, a per-driver
  `AssemblyLoadContext`, `dbdatasync init` seeding three driver manifests, CI packing them, the
  golden-path suite running against loaded-not-linked drivers.
- Ruled out because `ExcludeAssets="runtime"` reaches the same acceptance bar (a version bump needs no
  DbDataSync rebuild) without any of that cost or risk to the existing test/CI shape.

## Open questions / risks

1. **Version fidelity at runtime, not compile time — resolved by a separate phase, not by this one.**
   `LibraryRegistry`'s resolver serves whatever is actually installed at `<repo>/libraries/<id>/lib/`,
   which can differ from the exact version `MsSqlDriver`/`PostgresDriver` were compiled against. .NET's
   default load context is permissive about this by simple name — it will generally accept a different
   version rather than refuse it — but a genuinely breaking change in the installed version surfaces as
   a runtime `MissingMethodException`, not a build error. The same risk `nuget-loaded-drivers.md`'s own
   "Version conflicts with the host" section already names for compiled plugins, now real for these two
   drivers as well even though they aren't loaded as plugins in the separate-assembly sense.
   `architecture/implementation/todo/phase-109j-library-compatibility-checking.md` answers this: a
   no-execution static check (`MetadataLoadContext` against the driver's own extracted member surface)
   at `library install`/`sync` and `config check` time, plus a connection-scoped "Validate library"
   action on the Connections page that stages and writes real synthetic rows through the driver's own
   real pipeline, against a real server. Not gated on 109h landing first — either order is fine.
2. **The connection-creation seeding hook (#3's second seam)** — `ApplyStateDatabase`'s own hook is
   fully specified above; the connection-path one names the candidate call sites but still needs
   confirming against the real current save flow at implementation time.

## How to verify when built

- `dotnet build` clean — confirms `ExcludeAssets="runtime"` doesn't disturb compilation anywhere it
  touches `SqlBulkCopy`/`SqlDbType`/`NpgsqlDbType`/`SqlConnectionStringBuilder`.
- `DbDataSync.Drivers.MsSql.Tests`/`.Drivers.Postgres.Tests`, run standalone (`dotnet test` on just that
  project, not the full solution), green — proves their own new direct reference actually restores what
  the transitive one used to provide.
- `dotnet publish src/DbDataSync.Api` — its output directory does **not** contain
  `Microsoft.Data.SqlClient.dll`/`Npgsql.dll` unless a library install already put one at
  `<repo>/libraries/.../lib/` first. The acceptance test that the exclusion actually took effect, not
  just that the build still compiles.
- A fresh deployment (a wiped `libraries/` directory, or a brand-new repo root) connects to a real
  MsSql or Postgres source/target with **no manual `library install` step** — the acceptance test for
  item 3, and the one that must not regress.
- A version bump — `library sync` to a newer `Microsoft.Data.SqlClient` — takes effect with no
  DbDataSync rebuild. The same acceptance bar the original doc set for Option B, now met without
  Option B's cost.
