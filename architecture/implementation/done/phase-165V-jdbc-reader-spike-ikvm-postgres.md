# Phase 165V — JDBC reader spike: import ClrKernel's IKVM JDBC bridge, read Postgres over pgJDBC

**Status**: Built and verified 2026-09-21. See the Retrospective below.
**Plan reference**: `architecture/planning/done/jdbc-driver-support.md` (the decisions this phase carries
out — IKVM not a real JVM, import the source rather than reference the package, reader before writer —
and its own "What to do first" section, which this phase *is*), `architecture/planning/done/additional-database-drivers.md`
(the parameter-placeholder and `ConnectionConfig`-shape questions this phase's dialect work answers for
real). Structural template: `architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`
and `phase-148-oracle-driver-trigger-audit-and-flashback.md`, though this phase is deliberately far
narrower than either — a spike, not a shippable driver. See that doc's own risk list before reading
further; this phase exists to retire four of those risks with evidence instead of assumption.

## Why this phase is scoped the way it is

The planning doc's "What to do first" already named the shape: read one table from an already-tested
engine over JDBC, through the generic pipeline, with a `.jar`, comparing against that engine's native
driver on identical data. This phase is exactly that, not a step toward it — no writer, no provisioner,
no trigger-audit reader, no real catalog/metadata browsing, no CLI/web-console wiring, no second engine.
Anything this phase's own findings argue for gets a follow-up phase, the same way phase 147/148 spun off
their own "not built" items rather than growing the scope after the fact.

Four real unknowns motivated this spike. Three are now answered — by a real, disposable probe against
the actual `dbdatasync-postgres` container this repo already runs, not by reading source or guessing —
before writing a line of the real implementation:

1. ~~Does IKVM's JVM actually start and run pgJDBC on Linux~~ **Yes, confirmed, and the concern itself
   was wrong** — see "Confirmed by a real probe" below. `IkvmConfiguration.cs` is not needed at all.
2. ~~Does a value survive the round trip `Postgres → pgJDBC → java.sql.Types → JdbcDataReader → CLR`~~
   **Confirmed working** for `int`, `varchar`, `numeric` and `timestamp`, NULL included, against real
   rows in a real table — see below. The one real wrinkle found (not a blocker): `getTimestamp()`'s
   returned instant depends on the JVM's default time zone the same way `java.util.Date` always has;
   this phase's own comparison-against-Npgsql test is exactly what would catch a container whose JVM and
   .NET process disagree about local time zone, so it stays a thing the tests check for, not a thing
   assumed away.
3. ~~What `WatermarkReader`'s existing named-parameter call sites actually need~~ **Resolved by reading
   the source** (Open question 1, now closed) **and confirmed live**: a real `PreparedStatement` with a
   real bound `setTimestamp` parameter correctly filtered rows against the live container (excluded the
   row exactly at the boundary, included the two after it) — the mechanism itself is proven; only
   `JdbcCommand`'s own name→position translation (not yet written) remains implementation, not risk.
4. Whether the imported code, once it actually runs, has correctness bugs review alone doesn't catch —
   review already found one (Open question 4) before a line of it ran; the probe's plain-`Statement`
   path independently confirmed `wasNull()` is the right check (both `NULL` cells in the test data came
   back correctly only because the probe checked it explicitly) — exactly the check the imported
   `JdbcDataReader`'s typed getters skip.

## The imported source

`ClrKernel.Database.Provider.Jdbc` (Apache-2.0, `github.com/ClrKernel/ClrKernel`,
`src/ClrKernel.Database.Provider.Jdbc`, read in full at the current tip of `main` before writing this
doc). It is explicitly marked experimental and Windows-only by its own doc comments and has never been
run against a real driver on any platform — this phase is the first time any of it executes for real,
on any OS.

**Imported, into `src/DbDataSync.Drivers.Jdbc/Imported/`, adapted rather than referenced (per the
planning doc's "Import the source, don't reference the package"):**

- `JdbcConnection.cs` — ported near-verbatim. `ServerVersion`/`DataSource` still throw
  `NotImplementedException`; this phase does not need them (connection *testing*, phase 19's concern, is
  out of scope here) but leaves a `// TODO(phase 19 follow-up)` rather than filling them in speculatively.
- `JdbcCommand.cs` — ported, then genuinely extended: this is where the parameter-binding work named in
  the planning doc actually lands. Today it executes through `java.sql.Statement` and both
  `DbParameterCollection` and `Prepare()` throw. This phase adds a `PreparedStatement`-backed path:
  `CreateDbParameter`/`DbParameterCollection` become real, `ExecuteDbDataReader` switches to
  `_connection.prepareStatement(sql)` plus positional `setObject(index, value, javaSqlType)` calls when
  parameters are present, falling back to the existing `Statement` path when they are not (so
  `BatchReloadReader`, which binds nothing, is unaffected).
- `JdbcConnectionStringBuilder.cs` — ported as-is. Reserves `JdbcDriver`/`JdbcUrl`; everything else
  becomes a `java.util.Properties` entry, `user`/`password` included — exactly the shape the planning doc's
  connection-model table already committed to.
- `JdbcProviderFactory.cs` — ported as-is (`FromJarPath`, `FromAssemblyPath`, the per-driver-class
  factory registry via `URLClassLoader`).
- `IkvmConfiguration.cs` — **not imported.** `EnsureConfigured()` only does anything (walking the NuGet
  cache to set `AppContext.SetData("IKVM.Home", ...)`) when neither `AppContext.GetData("IKVM.Home")` nor
  an `ikvm.properties` file is already present — and confirmed by actually building a throwaway console
  app against `PackageReference Include="IKVM" Version="8.11.2"` and running it (see below): the modern
  `IKVM` package's own build targets already stage a complete per-RID JRE image
  (`<output>/ikvm/linux-x64/{bin,lib}/...`, `libikvm.so`, and a relative `ikvm.properties` with
  `ikvm.home.root=ikvm`) directly next to the built assembly, and IKVM finds it with **zero**
  configuration — no `IKVM.Home` set, works from any process working directory. `IkvmConfiguration`'s
  NuGet-cache walk, and its `win-x64` default, exist only for ClrKernel's own interactive-notebook
  scenario (`#r "nuget: ..."` loading an assembly with no normal build-output directory at all), which a
  normally-built, normally-published DbDataSync driver never has. Nothing needs porting or fixing here —
  the workaround this class exists for does not apply.
- `JdbcDataReader.cs` — ported, then fixed: see Open question 4. `GetDateTime`/`GetDecimal` call
  `ToDateTime`/`ToDecimal` directly on the JDBC result without the `wasNull` check
  `JdbcResultToClrObject` (used by the generic `GetValue`/`this[i]` path) already has, so a `NULL`
  `DATE`/`NUMERIC` column read through the typed getter throws instead of returning `DBNull`. `Read()`
  paths in this repo's readers go through `GetValue`, which is already correct — but leaving the typed
  getters broken invites a real bug the moment anything calls them directly, so this phase fixes them
  rather than carrying the gap forward.

**Also not imported** — `Jdbc.cs`, `JdbcConnectionProvider.cs`, `PluginExport.cs`: ClrKernel's own fluent
`DataSource`/`Query` API, its `ConnectionProviderDescriptor` UI-description mechanism, and a notebook
assembly-export attribute. None of it is ADO.NET; `JdbcDriver` below composes the imported ADO.NET types
directly, the same way `PostgresDriver`/`MySqlDriver` compose `Npgsql`/`MySqlConnector` types — no
ClrKernel-specific plumbing is needed or wanted.

## Confirmed by a real probe (not part of this repo)

Before writing any of the real driver, a throwaway console app (`PackageReference Include="IKVM"
Version="8.11.2"`, no other dependency) was built and run against `dbdatasync-postgres`, the same
container `DbDataSync.Drivers.Postgres.Tests` already uses (`localhost:15432`, confirmed already running
in this environment) with a real table (`jdbc_spike`: an int key, a `varchar`, a nullable `numeric(10,2)`,
a `timestamp`, three rows including a NULL in two different columns):

- **IKVM needs no configuration at all.** `dotnet build -r linux-x64` alone produces a working
  `java.util.Properties`/`java.sql.*` runtime — no `AppContext.SetData("IKVM.Home", ...)`, no
  `ikvm.properties` written by hand. Ran correctly from an unrelated working directory too.
- **pgJDBC's current plain jar is Java-8 bytecode.** `postgresql-42.7.13.jar` from Maven Central (no
  `jre7`/`jre8` classifier — those were dropped; Maven Central confirms only the plain jar exists for
  this version) has `Driver.class` at bytecode major version 52 (Java 8) — loadable by IKVM (Java SE 8
  only) as-is. This resolves the planning doc's "confirm the exact build" note for Postgres, concretely.
- **A real connection, a real `Statement` read, and a real `PreparedStatement` bind all worked**, loading
  the driver via `URLClassLoader`/`Class.forName` exactly the way the imported `JdbcProviderFactory`
  already does: `SELECT ... FROM jdbc_spike` returned all three rows with both NULLs reported correctly
  via `wasNull()`; `SELECT ... WHERE created_at > ?` with a bound `java.sql.Timestamp` correctly excluded
  the row exactly at the boundary and included the two after it.

This retires the risk this phase exists to retire. What is left is implementation (the imported classes,
adapted; `JdbcCommand`'s new `PreparedStatement`/parameter-translation path; the dialect; the driver; the
real test project) — not open research questions.

## What this phase will build

### `src/DbDataSync.Drivers.Jdbc/DbDataSync.Drivers.Jdbc.csproj`

References `DbDataSync.Core`, `DbDataSync.Drivers.Abstractions`, `DbDataSync.Drivers.Generic` (the same
three every compiled driver references) plus `PackageReference Include="IKVM" Version="8.11.2"`. **Not**
`ExcludeAssets="runtime"` — unlike Npgsql/MySqlConnector, IKVM is the JVM shim the imported code calls
directly (`IKVM.Runtime`, the `java.sql.*`/`java.util.*` surface), not a swappable operator-supplied
client library resolved later through `LibraryRegistry`; it has to actually be present at runtime for any
of this to work.

### `JdbcDialect` (`src/DbDataSync.Core/Sql/JdbcDialect.cs`)

- `ParameterReference` — renders a named marker (`@name`, matching every other dialect's look) rather
  than a bare `?`. Confirmed by reading `WatermarkReader`/`GenericValueBinder`: parameters are added to
  `DbParameterCollection` by name, in an order that does not reliably match the order their markers
  appear in the rendered SQL text (e.g. `AddParameter` for the row-limit cap is always called before the
  conditional previous-watermark parameter, regardless of which one's marker appears first in the
  `WHERE`/`LIMIT` clauses) — every existing provider (SqlClient, Npgsql, MySqlConnector, Oracle's) binds
  by name at the provider layer, so the C#-side add order never had to matter until now. JDBC's
  `PreparedStatement` has no such layer — only ordinal `?` in strict left-to-right text order — so
  `JdbcCommand` has to do the name→position translation itself: scan `CommandText` for each parameter's
  own `@name` marker, left to right, replace each occurrence with `?`, and `setObject` positionally in
  that scan order. This is exactly the shape .NET's own `OdbcCommand` already solves the identical
  problem with (ODBC is likewise ordinal-`?`-only) — real precedent, not a novel mechanism.
- `ToCanonicalType`/`RenderColumnType` — only what the spike's one test table actually needs (a handful
  of pgJDBC/`java.sql.Types` names: at minimum an integer key, a `varchar`, a `timestamp`, a `numeric`).
  Not a full JDBC type matrix — that question (open question 1 in the planning doc: is a JDBC engine ever
  its own `JdbcDriverSpec`, generalized across drivers, or does each JDBC-backed engine need its own
  dialect the way Postgres/MySql/Oracle each have one today) stays open past this phase, on purpose.

### `JdbcDriver` (`src/DbDataSync.Drivers.Jdbc/JdbcDriver.cs`)

Minimal `IDriver`: `CreateConnection` takes `ConnectionConfig.ConnectionString` as a raw JDBC URL — the
existing `AddressMode.connectionString` escape hatch, not a new URL-template mechanism (a URL template
per engine is real future work the planning doc names, out of scope while there is exactly one JDBC
engine under test) — with `Properties` copied onto `java.util.Properties` and `UserId`/`Password` set as
the `user`/`password` properties, never appended to the URL. `Readers = [BatchReloadReader,
WatermarkReader]` only; no `TriggerAuditReader` (no shadow-table DDL exists for this path), no writers,
no staging providers, no `IProvisioner`, no `ITableCatalog` beyond what the test harness hardcodes for its
one table (real catalog/metadata browsing is the planning doc's own still-open question, not this
phase's to answer). **Not registered** in `BuiltInDrivers`/`DbDataSyncHost`/`TaskRunner`'s composition
roots — this driver is exercised only from its own test project, deliberately not reachable from the CLI
or web console yet.

### `tests/DbDataSync.Drivers.Jdbc.Tests/`

New test project, `Category=Integration`, against the same Postgres service `docker-compose.yml` already
runs for `DbDataSync.Drivers.Postgres.Tests` — no new container. Two tests carry the spike:

1. **`BatchReloadReader` parity** — read a fixed test table over JDBC and over Npgsql, assert identical
   rows (by content, not row count). No parameters bound; this is the "does IKVM/pgJDBC even work at all
   on Linux" checkpoint, and gates the second test.
2. **`WatermarkReader` parity** — same table, a `timestamp` watermark column, two successive batches
   (forcing the watermark to advance and a parameter to actually bind). This is what exercises the new
   `PreparedStatement` path and the type round-trip for the column types the table has.

pgJDBC (not MySQL/MariaDB/MsSql/Oracle's driver) per the planning doc's own reasoning: pure Java, no
native components, permissive license, and the Postgres path through the generic pipeline is already the
most exercised one to compare against.

## What this phase does not build

- No writer, no provisioner, no trigger-audit reader.
- No real catalog/metadata browsing via `DatabaseMetaData` — the spike's table shape is hardcoded.
- No CLI/web-console wiring, no connection-editor UI entry.
- No `jars/` (or `libraries/`) artifact-management mechanism — the test harness loads its jar from a
  fixed path (see Open question 2), not through `LibraryRegistry`.
- No IKVM-compiled-`.dll` path — only `.jar`/`URLClassLoader`, the default and more robust form per the
  planning doc.
- No second JDBC-backed engine, and no decision on whether the eventual shipped driver is a hand-written
  `IDriver` (this spike's shape) or something built on `GenericDriverSpec` — the survey behind this doc
  confirmed `GenericDriverSpec`/`GenericConnectionStringKeys` is exclusively the descriptor-loader
  (`driver.yaml`) path today, and every compiled built-in (Postgres/MySql/MsSql/Oracle) hand-writes its
  `IDriver` instead — this phase follows that same precedent for the spike, but a real decision for the
  shipped driver is a follow-up once results are in.

## Open questions

1. ~~**Positional `?` vs. `SqlDialect`'s named-parameter shape.**~~ **Resolved and proven live** (see
   `JdbcDialect` above and "Confirmed by a real probe"): named markers in the rendered text, scanned
   left-to-right and translated to ordinal `?`/`setObject` at execute time inside `JdbcCommand`, the same
   approach `OdbcCommand` already uses. What's left is only that `JdbcCommand`'s own scan-and-replace
   code is written and exercised against a real multi-parameter query (the bounded-watermark-read case
   has two) — implementation, not a design question.
2. **Where the test pgJDBC jar comes from.** Every other engine's client library in this repo is resolved
   through NuGet (`ExcludeAssets="runtime"`) or `LibraryRegistry`, never a committed binary. A `.jar` is
   neither. Candidates: download at test/restore time (e.g. an MSBuild target hitting Maven Central,
   confirmed reachable from this environment, at the exact version confirmed above —
   `postgresql-42.7.13.jar`, no classifier needed) into an ignored path, vs. committing it under a
   `tests/.../fixtures/` path (pgJDBC is BSD-2-Clause, redistribution is fine, but a committed binary is
   against this repo's own grain). Leaning download-at-build-time; not yet decided.
3. ~~**IKVM's real RID-folder name on Linux.**~~ **Moot.** The concern this question was about — needing
   to know IKVM's Linux layout to fix `IkvmConfiguration`'s `win-x64` hardcode — does not apply: the
   modern `IKVM` 8.11.2 package's own build targets stage everything needed next to the build output on
   every RID automatically, confirmed by actually building and running against it (see above).
   `IkvmConfiguration.cs` is not imported at all, so there is nothing left to fix.
4. **`JdbcDataReader`'s null-handling gap** (described above, found by reading the imported source
   before any of it ran, and indirectly confirmed by the probe checking `wasNull()` explicitly where the
   affected getters do not) — fixed as part of the import in this phase, not filed as a separate
   follow-up, since it's needed for this phase's own tests to be trustworthy.

## How to verify when built

- `dotnet test --filter Category=Integration` on the new project, against the real docker-compose
  Postgres service — not mocked, the same posture every other driver phase in this repo takes.
- The `BatchReloadReader` parity test passes with real rows compared, not just "it ran."
- The `WatermarkReader` parity test passes across two batches with a real parameter bound through a real
  `PreparedStatement` — the thing the planning doc named as the actual blocker, and already proven live
  by the probe; this is confirming the real (not throwaway) implementation, not re-derisking the concept.
- Open questions 2 and 4 above are answered for real in the Retrospective this doc gets once built, the
  same way phase 147/148 recorded what they actually found against what they assumed going in.

# Retrospective

Built as designed above, with three real bugs found by running the actual parity tests against
`dbdatasync-postgres` — not by the earlier probe or by inspection — and one open question resolved along
the way.

## What shipped

- `src/DbDataSync.Drivers.Jdbc/Imported/`: `JdbcConnection`, `JdbcConnectionStringBuilder`,
  `JdbcProviderFactory`, `JdbcDataReader` (adapted from `ClrKernel.Database.Provider.Jdbc`), plus
  `JdbcParameter`/`JdbcParameterCollection` (new — the imported code had no parameter support at all) and
  a rewritten `JdbcCommand` with the `PreparedStatement`/name-to-position translation path this phase
  exists to build. `IkvmConfiguration.cs` was not imported — see "Confirmed by a real probe" above; a
  normal build needs none of it.
- `src/DbDataSync.Core/Sql/JdbcDialect.cs`, `src/DbDataSync.Drivers.Jdbc/JdbcCatalog.cs` (reuses
  `InformationSchemaQueries`, unmodified) and `JdbcDriver.cs` (reuses `GenericValueBinder` — no
  driver-specific `ISegmentValueBinder` was needed; see that class's own doc comment).
- `tests/DbDataSync.Drivers.Jdbc.Tests/`: `BatchReloadReaderAsync` and `WatermarkReaderAsync` (two
  batches, a real bound parameter on the second), both passing against the live container, both
  comparing row-for-row against `PostgresDriver`'s own published readers over Npgsql.
- Neither project is referenced by `BuiltInDrivers` or either composition root — exactly as scoped.

## Finding 1: `SqlDialect.ParameterName`'s default carries the sigil; `JdbcCommand` needed the bare form

`WatermarkReaderAsync` failed on its first real run: `CommandText references parameter
'@previousWatermark' with no matching entry in Parameters`. `SqlDialect.ParameterName(name)` defaults to
`ParameterReference(name)` — `"@name"`, sigil included — which is fine for every other dialect here
because the real ADO.NET provider underneath (SqlClient, Npgsql, MySqlConnector) matches a
`DbParameter.ParameterName` carrying that sigil against the token in the SQL text on its own. `JdbcCommand`
*is* that matching layer for JDBC, and its regex capture group strips the `@` before comparing. Fixed by
overriding `JdbcDialect.ParameterName` to return the bare name — precisely the carve-out
`SqlDialect.ParameterName`'s own doc comment already describes ("some providers want the bare name
without its sigil"). `BatchReloadReaderAsync` never hit this because it binds no parameters at all.

## Finding 2: `DateTimeOffset.LocalDateTime` stamps `Kind.Local`; Npgsql's own timestamps carry `Unspecified`

`WatermarkReaderAsync`'s first run (after fixing Finding 1) failed on a plain string comparison of the two
readers' `NewWatermark` values: `"...11:30:00.0000000"` vs `"...11:30:00.0000000-08:00"` — same instant,
different text, because `DateTime.ToString("o")` (what `WatermarkValue.Format` uses) only prints an offset
for `Kind.Local`/`Kind.Utc`, and the imported `JdbcDataReader.ToDateTime` used
`DateTimeOffset.FromUnixTimeMilliseconds(...).LocalDateTime`, which stamps `Kind.Local`. Every other
engine's `DateTime` for a `timestamp without time zone` column in this repo carries `Kind.Unspecified`
(`GenericValueBinder`'s own `CanonicalTypeKind.Timestamp` case does exactly this). Fixed with
`DateTime.SpecifyKind(..., DateTimeKind.Unspecified)` around the same conversion — the wall-clock value
was already correct (the earlier probe's manual inspection had no reason to notice the `Kind` tag; only a
real round-trip *comparison* surfaces a `Kind` mismatch). This is the concrete shape of the "one real
wrinkle" flagged before any code was written — found by the mechanism built to find it.

## Finding 3: a test-fixture bug, not a driver bug

`BatchReloadReaderAsync` and `WatermarkReaderAsync` share one `JdbcTestDatabase` (`IClassFixture`, one
database for the whole test class) but xUnit gives each `[Fact]` its own test-class instance, so a fixed
table name collided ("relation already exists") the moment the second fact's `InitializeAsync` ran.
Fixed with a per-instance `Guid`-suffixed table name, the same pattern `PostgresPipelineTests` already
uses for exactly this reason.

## Open question 2, resolved: the test jar is downloaded at build time

An MSBuild `DownloadFile` target in `DbDataSync.Drivers.Jdbc.Tests.csproj` fetches
`postgresql-42.7.13.jar` from Maven Central into `$(BaseIntermediateOutputPath)jdbc-jars/` (gitignored,
same as every other `obj/` output) on first build, and a `None`/`CopyToOutputDirectory` item places it
next to the test assembly as `postgresql.jar`. Not committed, matching this repo's own convention that a
client library is resolved (NuGet, `LibraryRegistry`) rather than checked in as a binary.

## What was checked for real, and what was not

Checked against the live `dbdatasync-postgres` container (not mocked): IKVM needs zero configuration on
Linux from a normal `dotnet build`/`dotnet test` (confirmed both by the standalone probe and by the real
test project's own output layout); pgJDBC's current plain jar (no `jre7`/`jre8` classifier) is Java 8
bytecode and loads under IKVM; a real `PreparedStatement` bind through the new `JdbcCommand` path produces
identical rows and an identical watermark to Npgsql across two successive incremental batches, `NULL`
included in two different columns and two different types (`varchar`, `numeric`).

Not checked, and still exactly what "What this phase does not build" says: a writer, a provisioner,
trigger-audit change tracking, real `DatabaseMetaData`-based catalog browsing for a JDBC-backed engine
that isn't Postgres, CLI/web-console wiring, a second JDBC engine, the `.dll`/IKVM-compiled artifact path,
and the `GenericDriverSpec`-vs-hand-written-`IDriver` question for whatever driver this eventually ships
as. All of that is real future work, not implied by anything built here.

## Addendum: IKVM excluded and resolved via `LibraryRegistry`, like every sibling driver's client library

Built initially with IKVM as a plain, un-excluded `PackageReference` — reasoned at the time that IKVM was
"the JVM shim the code calls directly," unlike Npgsql/MySqlConnector's swappable client libraries. That
reasoning didn't survive scrutiny: Npgsql also "has to be present at runtime for anything to work," which
is exactly what `ExcludeAssets="runtime"` + `RequiredLibraryId` + `libraries/<id>/lib/` + phase 109j's
surface-checking already exist to handle for a required dependency, and IKVM (a bundled JRE image, not
just a managed assembly) is if anything a stronger case for it than Npgsql, not a weaker one.

**`ExcludeAssets="runtime"` alone was not enough** — confirmed by building a throwaway probe with only
`runtime` excluded and inspecting the output: IKVM's bundled per-RID JRE image (`ikvm/<rid>/{bin,lib}/...`,
`ikvm.properties`) and native `libikvm.so` were still copied into the build output, because they come from
the package's own `buildTransitive` targets and its native-asset group — neither of which the `runtime`
exclusion touches. `ExcludeAssets="runtime;build;buildTransitive;native"` (confirmed by testing each
addition against the actual build output) suppresses all of it while leaving compile-time typing intact
(IKVM ships proper `ref/net8.0/` reference assemblies, the same mechanism Npgsql's own `ExcludeAssets`
already relies on).

**The deeper question — does IKVM's own relative-path resolution (`ikvm.properties`'s `ikvm.home.root=ikvm`,
resolved from wherever `IKVM.Runtime.dll` itself loads from) survive living in a directory the consuming
app never touches — was verified for real, twice:**

1. A hand-built resolver mirroring `LibraryRegistry.ArmResolver`/`EnsureResolverArmed` exactly, pointed at
   an IKVM package published into a separate `libraries/ikvm/lib/`-shaped directory: a full JDBC
   connection to the live container, through pgJDBC, succeeded — with the consuming app's own output
   directory carrying zero IKVM assets.
2. The same thing again through the *real*, unmodified production code: `LibraryInstaller.InstallAsync`
   (exactly what `dbdatasync config library install ikvm` runs), `LibraryRegistry.LoadAll()`, and a real
   `JdbcDriver` instance (compiled against the now-excluded `IKVM` reference) ran `SELECT 1` against the
   live container successfully — confirmed by listing the consuming app's own directory and finding no
   `IKVM.*`/`ikvm/`/`libikvm.so` in it at all.

**One real wrinkle, accepted rather than engineered around:** `KnownLibraries.LibraryCatalogEntry`/
`LibraryManifest.FactoryType` is a required, non-nullable field whose whole schema assumes a library is a
`DbProviderFactory`-bearing ADO.NET client — true for all seven existing entries, false for IKVM (a JVM
runtime, not a database client; the actual `DbProviderFactory` for JDBC is `DbDataSync.Drivers.Jdbc`'s own
`JdbcProviderFactory`, in a different assembly entirely). Rather than loosen that schema (nullable
`FactoryType`, skip-registration-when-absent) — a real change to a shared subsystem several other drivers
depend on, disproportionate to what this phase needs — the `ikvm` catalog entry's `FactoryType` names a
real, public type (`java.sql.Types, IKVM.Java`) purely so `DriverLibraryCompatibility.AssemblyNameFrom`
has an assembly name to derive (confirmed to be the *right* assembly: reflecting the real 8.11.2 packages
showed every `java.sql.*`/`java.util.*` type this driver's IL actually references lives in `IKVM.Java`,
not `IKVM.Runtime`, which is what makes phase 109j's surface-check meaningful here rather than vacuously
true against the wrong assembly). Nothing ever calls `DbProviderFactories.GetFactory("ikvm")`.
`dbdatasync config library list` will report `ikvm ... DOES NOT RESOLVE` — confirmed empirically that
`DbProviderFactories.GetFactory` throws `ArgumentException` for a type that isn't a `DbProviderFactory`,
which that command's own `TryResolves` already catches and reports as a status line, not a crash — a
known, cosmetic, accepted quirk of reusing a schema built for a different kind of library, not a
correctness problem.
