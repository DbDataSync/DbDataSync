# .NET database drivers loaded from NuGet

**Resolved 2026-09-13 — 6 of 9 phases shipped.** Phases 1–6 below
(`architecture/implementation/done/phase-109a-driver-id-is-a-string.md` through
`-109f-stateengine-string-and-registry.md`) are done and verified against real code: `ConnectionConfig.DriverType`
is a plain `string`, `GenericDriver` is public, the provider layer works end to end (restore-then-load,
`dbdatasync provider install/sync`, proven against `MySqlConnector` — a package referenced in **no**
`.csproj` in the solution), the YAML descriptor + `GET /api/drivers` ships and was tested against a real
MySQL container, compiled `IDriver` plugins load via a per-driver `AssemblyLoadContext` gated on
`IDriver.ContractVersion`, and `StateEngine` is a string-backed registry.

Phases 7–9 (dependency removal) remain, and each already has its own phase doc — this document does
not need to be revisited to create them:

- `architecture/implementation/todo/phase-109g-state-store-off-provider-packages.md` — `DbDataSync.State.csproj`
  still references `Microsoft.Data.SqlClient` and `Npgsql` directly, confirmed.
- `architecture/implementation/todo/phase-109h-builtin-drivers-off-provider-packages.md` — `MsSqlDriver`/
  `PostgresDriver` still reference their providers directly, confirmed. Deferred until 109g lands.
- `architecture/implementation/todo/phase-109i-duckdb-decoupling.md` — `DuckDB.NET.Data.Full` still
  referenced in `Verification`/`Scripting`, confirmed. Deferred, separate track.

Of the five open questions this document raised (grep `UNDECIDED`, below): Q1, Q3 and Q5 were resolved
during implementation; Q2 and Q4 are decided here — **Q2: no. Q4: not now.** See each question's own
entry for the reasoning.

The rest of this document — the phase breakdown, the descriptor/compiled-driver design, the worked
examples — stays as the design record phases 7–9 and any future engine work build from.

Goals, in order of value:

1. **Break the core's compile-time dependency on ADO.NET provider packages** (`Microsoft.Data.SqlClient`,
   `Npgsql`, `DuckDB.NET`), so a vendor's fix is a runtime package swap, not a new DbDataSync build.
2. **Let an operator add a whole new database engine** DbDataSync was not built with, by installing a
   NuGet package into a running deployment — no fork, no custom build, no PR to this repo.

Read `architecture/planning/done/additional-database-drivers.md` first: it is the plan for adding
engines *by writing driver projects in this solution*, and much of its "what the abstraction already
gives us for free" applies unchanged here. This document is about the delivery mechanism, not about
any one engine.

---

## Recommendation up front

Two related outcomes, in order of value:

1. **Stop the core referencing ADO.NET provider packages at compile time.** `DbDataSync.State`'s two
   server backends couple to `Microsoft.Data.SqlClient` / `Npgsql` in *one line each*; verification
   and scripting use `DuckDB.NET` only through near-`DbConnection` shapes. Route those through a
   **provider layer** — NuGet-restored provider assemblies registered by type-name string,
   `dbdatasync provider install / sync` — and a vendor's CVE fix is a package swap, not a DbDataSync
   build. See *Providers and drivers are two separable things* below.
2. **Load whole new engines from NuGet** — the original goal — as a **descriptor** (`driver.yaml`,
   no C#) or a **compiled driver** (a full `IDriver` in a package). Both ship; they serve different
   needs.

The delivery is nine phases, each independently shippable — see *Phased delivery*. Phases 1–5 build
the mechanism and prove it against a **server engine the build does not reference** (MySQL, via
`MySqlConnector`); nothing existing is removed until 6+. **SQLite stays a hard dependency
permanently**; **DuckDB stays one until Phase 9**.

Acquisition is identical throughout: a manifest lists packages, `install` / `sync` restores them into
`<repo>/{providers,drivers}/<id>/`, both processes load from there. No NuGet client in the running
host; nothing fetched from a bare config value.

---

## Phased delivery

Every phase compiles, passes CI, and is deployable on its own. Nothing existing is refactored away
until the mechanism is proven end to end against a non-embedded engine (phases 1–5). Each phase below
lists what it **builds**, what it **leaves alone**, and how it is **tested**.

### Phase 1 — driver id is a string

- **Builds:** `ConnectionDriverType` enum → `string` everywhere — `ConnectionConfig`/`ConnectionInput`,
  `DriverRegistry`'s key and signatures, the ~dozen API services, the SPA `DriverType` type.
- **Leaves alone:** `StateEngine` (Phase 6). The SPA picker still lists `MsSql`/`Postgres`/`DuckDb`
  from a constant. No loader, no new packages.
- **Tested by:** the whole existing suite (proves no behaviour change); one added test that an
  unknown `driverType` fails config validation with a message that will name the install command.
- Pure refactor; existing YAML (`driverType: MsSql`) round-trips byte-for-byte.

### Phase 2 — `public GenericDriver`

- **Builds:** extract the engine-neutral `IDriver` body implied by `PostgresDriver` +
  `DbDataSync.Drivers.Generic` into `public class GenericDriver : IDriver`, constructed from a
  `SqlDialect`, a `DbProviderFactory`, and a small record (which generic Kinds, catalog strategy).
- **Leaves alone:** the built-in drivers — `GenericDriver` is added, nothing is rewired to it.
- **Tested by:** `GenericDriverTests` — stand one up by hand against the existing Postgres container
  (`PostgresDialect.Instance` + `NpgsqlFactory.Instance`), watermark + batch round-trip.

### Phase 3 — the provider layer (no cutover)

- **Builds:** `<repo>/providers/<name>/` + a `provider.json` manifest (id, `packages[]`,
  `factoryType`); `dbdatasync provider install / sync / uninstall / list` (throwaway project →
  `dotnet restore` → copy to `lib/`); a `ProviderRegistry` that at startup calls
  `DbProviderFactories.RegisterFactory(name, "<factoryType>")` per entry, with
  `AssemblyDependencyResolver` for native assets. Runs in the API **and** the TaskRunner.
- **Leaves alone:** the core still hard-references `Microsoft.Data.SqlClient` / `Npgsql` /
  `DuckDB.NET`. An empty `providers/` makes the startup step a no-op.
- **Tested by:** `provider install MySqlConnector` (referenced nowhere in the solution), then assert
  `DbProviderFactories.GetFactory("MySqlConnector")` opens a connection to a new `mysql` service in
  `docker-compose`. The non-embedded-dependency proof.

### Phase 4 — the YAML descriptor + `GET /api/drivers`

- **Builds:** `<repo>/drivers/<id>/driver.yaml` → deserialised into a `GenericDriver` bound to a
  Phase 3 provider; `DriverRegistry` loads descriptor drivers at startup; `GET /api/drivers` (id,
  display name, built-in flag); the SPA driver picker reads it; `dbdatasync driver install / list`
  (seeds `driver.yaml` + triggers the provider install).
- **Leaves alone:** built-ins keep their compiled path and hard refs.
- **Tested by:** end to end — `provider install MySqlConnector` + a MySQL `driver.yaml`, a
  golden-path-style spec that creates the connection and a mapping and runs a **watermark replication
  MySQL → SQL Server** against containers, asserting the target matches. First "new engine, no
  rebuild."

### Phase 5 — compiled `IDriver` plugins + `Abstractions` as a package

- **Builds:** per-plugin `AssemblyLoadContext` with shared-contract resolution (`Abstractions`,
  `Core`, `System.Data.Common` → host); `DbDataSync.Drivers.Abstractions` published as a versioned
  NuGet package; `driver.json` (compiled variant) → load assembly, find the `IDriver`, register it.
- **Leaves alone:** deps, built-ins.
- **Tested by:** a tiny fixture `IDriver` plugin published to a CI-local feed, installed, loaded,
  round-trip; loader-isolation unit tests (shared type identity across the boundary; a deliberate
  transitive-version conflict). Settles Q3.

--- *below this line every phase removes a dependency; start only once 1–5 are in production* ---

### Phase 6 — `StateEngine` → string + `StateDialectRegistry` (deps still in place)

- **Builds:** `StateEngine` enum → string id; `StateDialect.For` → a registry; built-in dialects
  still registered in code and still hard-referencing their providers; `CreateConnection` routes
  through the provider layer for a non-built-in engine.
- **Tested by:** the existing cross-engine state suite, unchanged; a fixture `StateDialect` that
  registers and runs the full migration set.
- Refactor + additive registry; no dependency change.

### Phase 7 — state store off the provider packages

- **Builds:** `MsSqlStateDialect` / `PostgresStateDialect` `CreateConnection` → the provider factory;
  **remove `Microsoft.Data.SqlClient` and `Npgsql` from `DbDataSync.State.csproj`.** SQLite stays.
- **Operational change:** a SQL Server or Postgres *state* backend now needs
  `provider install Microsoft.Data.SqlClient` (the installer seeds both by default). Release note
  required.
- **Tested by:** the full cross-engine state suite, now with the providers *restored* rather than
  referenced.

### Phase 8 — built-in replication drivers off the provider packages

- The hard one — `MsSqlDriver`'s `SqlBulkCopy`, typed `SqlDbType` binding, `SqlException.Number`.
  Either they keep the reference (accepted: a SqlClient CVE then patches those paths only) or they
  become first-party plugins in `drivers/mssql/`. Decide when 1–7 are done and the real cost is on
  the table.

### Phase 9 — DuckDB decoupling (separate, later)

- `DbDataSync.Verification` / `.Scripting` / the DuckDb driver → the provider factory; drop the
  `DuckDB.NET` `PackageReference`. DuckDB's native assets still ship while the embedded engine is in
  use, so this is "independently updatable," not "removable." Deferred per review.

---

## Providers and drivers are two separable things

The first draft conflated "which engines are load-bearing" with "which provider packages the build
references," and concluded MsSql/Postgres/SQLite/DuckDB were stuck compiled in. Pulling those apart
is most of the actual value here.

- A **provider** is a third-party ADO.NET assembly — `Microsoft.Data.SqlClient`, `Npgsql`,
  `MySqlConnector`, `DuckDB.NET`, `Microsoft.Data.Sqlite`. It gives you a `DbProviderFactory` and a
  `DbConnection` subclass and nothing DbDataSync-shaped.
- A **driver** is DbDataSync code — an `IDriver`, its readers/writers, its `SqlDialect` — or, for the
  state store, a `StateDialect`. It talks to an engine *through* a provider.

**The goal is to stop the core referencing providers at compile time**, so a vendor's fix to
`Microsoft.Data.SqlClient` or Npgsql is a `dbdatasync provider sync` (a new package version on disk),
not a new DbDataSync build. Drivers can then be updated on the same terms.

### How little actually couples to a provider

`DbConnection` / `DbCommand` / `DbParameter` / `DbTransaction` (BCL `System.Data.Common`) carry the
overwhelming majority of every consumer. The concrete-provider touchpoints:

| consumer | what it uses from the concrete provider | removable? |
| --- | --- | --- |
| `MsSqlStateDialect` | `new SqlConnection(cs)` — **one expression.** `ShouldRetry` returns `false`, no typed exception. | yes, trivially |
| `PostgresStateDialect` | `new NpgsqlConnection(cs)` — **one expression.** | yes, trivially |
| `SqliteStateDialect` / `StateDatabase` | `SqliteConnection`, `SqliteConnectionStringBuilder` (`Pooling=false`), `SqliteException` codes 5/6 in `ShouldRetry` | keep — SQLite is the default, offline, serverless; not worth decoupling |
| descriptor driver (Watermark / BatchReload / DeleteInsert) | nothing but `DbConnection` + a factory — that is the point of the descriptor | already decoupled |
| `MsSqlDriver` (compiled) | `SqlConnectionStringBuilder` (string assembly), `SqlBulkCopy` (staging fast path — no BCL equivalent), `SqlDbType` ×28 (typed segment-bound binding), `SqlException.Number == 3952` | hard — see below |
| `PostgresDriver` (compiled) | `NpgsqlDbType` ×17, `NpgsqlConnectionStringBuilder`, `NpgsqlConnection` | moderate |
| `DbDataSync.Verification`, `DbDataSync.Scripting` | `DuckDBConnection` / `DuckDBCommand` — mostly `DbConnection`-shaped | via factory, yes — DuckDB still ships (verification/segmenting need it), just independently updatable |

So: **the state store's server backends couple to a provider in exactly one line each.** The
compiled replication drivers couple meaningfully — `SqlBulkCopy` genuinely has no
`System.Data.Common` form, and typed `SqlDbType`/`NpgsqlDbType` parameter binding would have to fall
back to the generic `System.Data.DbType` with some loss.

### The provider layer

One mechanism, shared by everything above:

- `<repo>/providers/<invariantName>/` — a NuGet-restored provider assembly + closure + lockfile,
  installed by `dbdatasync provider install <packageId>[ …]` / kept current by `provider sync`. Same
  restore-then-load machinery as a driver (they can literally be the same command with a `--kind`).
- Registered by **type-name string**, not a compile-time reference:
  `DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient",
  "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient")`. The assembly only has to
  be *loadable*, not *referenced*. `DbProviderFactories.GetFactory(name)` then hands any consumer a
  `DbProviderFactory`.
- Provider assemblies load into the **default context / one shared ALC**, not a per-driver one — so
  the state store's `SqlConnection` and a driver's `SqlConnection` are the same type. (Native assets
  like SqlClient's SNI shim and DuckDB's libs need the same `AssemblyDependencyResolver` handling the
  driver loader already requires — so the provider layer and the driver loader are one loader.)
- This **dissolves the version-coordination worry** the first draft raised: the state store and the
  MsSql driver resolve `Microsoft.Data.SqlClient` from the *same* registration. One copy, one
  version, updated in one place.

`DbDataSync.State.csproj` then references only `DbDataSync.Core` and `Microsoft.Data.Sqlite`.
`DbDataSync.Verification` / `DbDataSync.Scripting` drop their `DuckDB.NET` `PackageReference` for a
factory lookup. The base package still carries SQLite (and DuckDB's native libs while verification
uses the embedded engine), so it does not shrink much — but a `Microsoft.Data.SqlClient` advisory
stops meaning a patch release.

### Custom state dialects as an extension

`StateDialect` is already the right seam — an `abstract class` with a static `For(engine)` and ~10
abstract members that the `Migrations` token set (`{{text}}`, `{{key}}`, `{{int}}`,
`{{identity:Id}}`, `{{addcolumn}}`, `{{dropindex}}`) is the contract for. To make it extensible:

- `StateEngine` enum → a string id (the same move Phase 1 makes for `ConnectionDriverType`).
- A `StateDialectRegistry` mirroring `DriverRegistry`: built-ins registered in code; a third-party
  `StateDialect` subclass discovered from an installed driver/plugin package.
- `StateDatabase` takes the resolved `StateDialect` (it already does) and the provider factory from
  the provider layer.

A custom state dialect stays a **compiled** artifact — it is not a descriptor. Of the two existing
server dialects, ~5% is descriptor-shaped and *already shared* (`.Sql` is the replication
`MsSqlDialect`/`PostgresDialect`; `ParameterName` / `Limit` / `CreateConnection` are the only others);
~15% is canonical → native DDL for four frozen tokens (the opposite direction to the descriptor's
native → canonical type map — `SqliteSqlDialect` doesn't even implement `RenderColumnType`); and ~80%
is the `MERGE … WITH (HOLDLOCK)` idiom with its partial-index-predicate qualifier (~75 of
`MsSqlStateDialect`'s 168 lines) plus `DropIndex` / `Get,SetSchemaVersion` / the retry predicate —
migration bookkeeping with no replication concept and no declarative form. `StateDialect`'s own doc
comment already made this call, citing the same "structural things don't belong in the shared
dialect" rule the descriptor plan runs on.

So it is a small, bounded compiled artifact — `MsSqlStateDialect` (168 lines) is the worked example —
and "Oracle as a state backend" is that plus opening the enum: real work, gated on someone actually
wanting an added engine to hold DbDataSync's operational data, but a clean pattern rather than a fork.

### What still can't be decoupled, and why

Even with the provider layer, some driver *logic* stays compiled and hard-referenced:

- **The built-in compiled drivers' fast paths.** `MsSqlDriver`'s `SqlBulkCopy` staging and typed
  binding, `MsSqlChangeTrackingReader` / `MsSqlCdcReader`. Two honest options: (a) keep them compiled
  — a SqlClient CVE then means a patch build *for those paths only*, while the state store and every
  descriptor driver are already covered by a `provider sync`; or (b) move them to first-party plugins
  carrying their own provider in `drivers/mssql/lib/`, so a SqlClient bump is `driver sync`. (b) is
  the complete version of the goal and the larger job.
- **`Microsoft.Data.Sqlite`** — the default state store, offline and serverless. Decoupling it buys
  nothing.

Package size (~150 MB, mostly DuckDB's five-platform native libraries) is a separate question about
DuckDB's native assets (per-RID packages, trimming), not about this design.

---

## What "a NuGet driver" can mean — two forms

These are different enough that conflating them is the main way this feature goes wrong.

### The descriptor — a bare ADO.NET provider plus config

`Oracle.ManagedDataAccess.Core`, `MySqlConnector`, `Microsoft.Data.Sqlite`, `System.Data.Odbc`,
`FirebirdSql.Data.FirebirdClient` — each is just a `DbProviderFactory` and a `DbConnection` subclass,
a contract that has not changed since .NET Framework 2.0. DbDataSync already has everything else an
engine needs *if* it is told the engine's dialect:

- `DbDataSync.Drivers.Generic` has `WatermarkReader`, `BatchReloadReader`, `SegmentExpansion`,
  `BatchInsertStagingProvider`, `DeleteInsertWriter`, `InformationSchemaQueries`, the
  create/alter-table planners, verification, natural-key derivation — all of it engine-neutral,
  parameterised on a `SqlDialect`.
- `PostgresDriver` is *already* mostly this: it registers the generic `Watermark`, `BatchReload`,
  `StagingTable` and `DeleteInsert` Kinds and supplies a `PostgresDialect`. The only reason it is a
  compiled project is that `PostgresDialect` is code.

So the descriptor is: **make the dialect data instead of code.** A `driver.yaml` in the config repo
declares identifier quoting, parameter placeholder style, the catalog-query flavour
(`information_schema` / Oracle `ALL_*` / ODBC `GetSchema`), a native↔canonical type map, and the
provider's factory type. DbDataSync restores the named ADO.NET provider package, loads its factory
by name, and stands up a generic driver against the declared dialect.

**Against the grain, and worth saying so.** `SqlDialect`'s own doc comment: *"This is deliberately
not an attempt to abstract over engines… If generalising something here would need a flag per
engine, that is the signal it does not belong here."* the descriptor is exactly the generic-SQL abstraction
that comment argues against. The counter-argument is scope: it is **watermark and batch only**,
same limit `additional-database-drivers.md` already accepts for hand-written drivers — no CDC, no
engine-specific upsert, no identity gymnastics. Within that box the dialect surface that actually
varies is small (quoting, placeholders, `LIMIT`/`FETCH`, `information_schema` quirks, type names),
and the descriptor stays declarative. The moment an engine needs more than the box holds, it wants a
compiled driver, and that is the honest signal to write one.

### The compiled driver — a full `IDriver` in C#

A .NET assembly that references a published `DbDataSync.Drivers.Abstractions` package and implements
`IDriver` plus whatever `IChangeReader`/`IStagingProvider`/`IChangeWriter`/`IProvisioner` variants
the engine warrants — the same code a driver project in this solution contains, shipped as a NuGet
package and loaded at runtime instead of compiled in.

This is the clean long-term extensibility story and the one the architecture doc's §5 already
gestures at ("implement `IDriver`… and register it in the driver registry. No changes to
`DbDataSync.Api`, `DbDataSync.TaskRunner`, or the SPA"). The cost is real: `Abstractions` becomes a
**public, semver-committed contract**, and every plugin is arbitrary code in the API process.

---

## Worked examples — the same engine, both ways

### The descriptor (MySQL, watermark/batch only)

Everything DbDataSync needs to run a generic driver against MySQL, as git-tracked config. No compile.

```yaml
# <repo>/drivers/mysql.generic/driver.yaml
id: mysql.generic
displayName: MySQL / MariaDB (generic)

# Direct NuGet package refs. `dbdatasync driver install` restored these and their
# transitive closure into <repo>/drivers/mysql.generic/lib/. A list, not a single id:
# some engines split the provider across assemblies or ship native-asset packages
# that are not transitive dependencies (Oracle, DB2).
provider:
  factoryType: "MySqlConnector.MySqlConnectorFactory, MySqlConnector"
  packages:
    - { id: MySqlConnector, version: "2.4.0" }

dialect:
  quoteIdentifier: backtick        # backtick | doubleQuote | bracket
  parameterPrefix: "@"             # "@" -> @p , ":" -> :p , "?" -> positional
  rowLimit: limitOffset            # LIMIT n OFFSET m   (vs. offsetFetch for OFFSET..FETCH)
  catalog: informationSchema       # informationSchema | oracleAllTables | odbcGetSchema
  supportsChangeDatabase: true     # false -> a mapping naming another database is a config error

# Native type name (with its (p,s) args) -> canonical. Anything unlisted -> Unmappable,
# which provisioning reports as unsupported rather than guessing a rendering.
typeMap:
  tinyint:        Int8
  smallint:       Int16
  int:            Int32
  bigint:         Int64
  "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
  double:         Double
  "varchar(n)":   { kind: String, length: n, unicode: true }
  text:           { kind: String, max: true }
  datetime:       Timestamp
  date:           Date
  json:           Json
  blob:           { kind: Binary, max: true }

# Engine-neutral strategies to offer. All already exist in DbDataSync.Drivers.Generic.
capabilities:
  readers: [Watermark, BatchReload]   # WatermarkReader, BatchReloadReader + SegmentExpansion
  staging: [StagingTable]             # BatchInsertStagingProvider (multi-row INSERT)
  writers: [DeleteInsert]             # DeleteInsertWriter — no primary key required, portable
```

That is the whole driver. It appears in the connection editor's engine picker and every
reader/cache/writer picker with no code and a reviewable diff.

### A compiled driver (MySQL, with binlog CDC)

The same engine, but now we want delete-detecting incremental sync off the binlog. Shipped as a
NuGet package that references the published `DbDataSync.Drivers.Abstractions`.

```csharp
// MyCompany.DbDataSync.Drivers.MySqlBinlog
public sealed class MySqlBinlogDriver : IDriver, IConnectionTester, IProvisioner
{
    public string DriverType => "mysql.binlog";
    public int? DefaultPort => 3306;

    // The bespoke reader is the only part that had to be written; the rest reuses Generic.
    public IReadOnlyList<IChangeReader>    Readers          { get; } = [new MySqlBinlogReader()];
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [new BatchInsertStagingProvider(MySqlDialect.Instance)];
    public IReadOnlyList<IChangeWriter>    Writers          { get; } = [new DeleteInsertWriter(MySqlDialect.Instance)];

    public DbConnection CreateConnection(ConnectionConfig c, string? credential) =>
        new MySqlConnection(BuildConnectionString(c, credential));

    public Task<IReadOnlyList<string>>         ListDatabasesAsync(...) => /* SHOW DATABASES */;
    public Task<IReadOnlyList<TableMetadata>>  ListTablesAsync(...)    => /* information_schema */;
    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(...)   => /* information_schema */;
}

// What no descriptor can express: turning a proprietary change stream into ChangeRows.
public sealed class MySqlBinlogReader : IChangeReader, IPositionCapturing
{
    public string Kind => "MySqlBinlog";
    public bool DetectsDeletes => true;                 // binlog ROW events carry the deleted row

    public async IAsyncEnumerable<ChangeRow> ReadChangesAsync(
        DbConnection conn, SourceTableRef src, string? fromGtid, ReadIntent intent, ...)
    {
        // open a binlog stream from `fromGtid`, decode WRITE/UPDATE/DELETE_ROWS events for `src`,
        // yield one ChangeRow each, and hand back the new GTID as the watermark.
        // A few hundred lines of protocol handling — none of it declarative.
    }
}

// The dialect as code, because the type mapping genuinely branches:
public sealed class MySqlDialect : SqlDialect
{
    public override string QuoteIdentifier(string id) => $"`{id.Replace("`", "``")}`";
    public override string ParameterReference(string name) => $"@{name}";

    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (name, args) = CanonicalTypeSpec.Parse(nativeType);
        return name switch
        {
            // tinyint(1) is MySQL's boolean; tinyint(4) is a number. A typeMap keyed on the
            // name alone cannot tell them apart.
            "tinyint" when CanonicalTypeSpec.IntAt(args, 0, 4) == 1 => Simple(CanonicalTypeKind.Boolean),
            "tinyint"                                               => Simple(CanonicalTypeKind.Int8),

            // unsigned bigint overflows Int64 — widen, and say so.
            "bigint" when nativeType.Contains("unsigned")
                => new CanonicalType(CanonicalTypeKind.Decimal, null, 20, 0, false, false,
                       SourceNote: "MySQL unsigned bigint exceeds Int64; widened to decimal(20,0)."),
            "bigint" => Simple(CanonicalTypeKind.Int64),

            "datetime" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null,
                              CanonicalTypeSpec.IntAt(args, 0, 0), false, false),
            _ => Simple(CanonicalTypeKind.Unmappable),
        };
    }

    public override RenderedColumnType RenderColumnType(CanonicalType t) => /* the reverse, also branching */;
}
```

`PostgresDialect.ToCanonicalType` / `RenderColumnType` in this repo is the existing proof of how much
conditional logic lives here in practice — `money` carrying currency semantics a generic decimal
loses, `Int8` widening because Postgres has no 1-byte integer, one digit of `datetime2` sub-second
precision dropped on the way to `timestamp(6)`, each with its own `SourceNote`. None of that is a
lookup table.

---

## When each is the right choice

**The descriptor is enough when all of these hold:**

- the engine has a maintained `DbProviderFactory` on NuGet;
- watermark and/or batch-reload is acceptable — an append-only or append/update-only source, or one
  where a periodic reconciling reload is fine (`additional-database-drivers.md` makes the same
  concession for hand-written drivers);
- the native type vocabulary maps from the type *name* plus its `(p,s)` arguments, without looking
  at a value, a flag word, or surrounding context;
- `information_schema` — or one of the two other named catalog strategies — describes its tables;
- writing with `DeleteInsert` (plain `DELETE … WHERE scope` + `INSERT … SELECT`, no primary key) is
  acceptable.

You get a driver in a ~30-line reviewable commit, no build step, and `DbDataSync.Drivers.Abstractions`
never becomes a contract you have to version.

**You need a compiled plugin the moment any of these is true:**

- **A real change-data reader** — binlog, LogMiner, Flashback Version Query, a vendor CDC feed.
  Decoding a change stream into `ChangeRow`s is code. This is the usual reason.
- **Type mapping that branches** — `tinyint(1)` vs `tinyint(4)`, `unsigned` widening, a type that
  carries semantics its name doesn't, precision-loss annotations.
- **An engine-specific write** — `MERGE`, `INSERT … ON DUPLICATE KEY UPDATE`, `OVERRIDING SYSTEM
  VALUE`, identity/sequence handling, direct-path or `COPY` bulk load.
- **A catalog that fits none of the named strategies**, or one that needs `DbConnection.GetSchema`
  massaging (ODBC).
- **Provisioning** — `IProvisioner`, i.e. create/alter target-table DDL in the engine's grammar.
- **Connection assembly beyond host/port/database** — Oracle TNS names, an ODBC DSN, a wallet.

A plugin still reuses the generic pieces — `WatermarkReader`, `BatchInsertStagingProvider`,
`DeleteInsertWriter`, `InformationSchemaQueries`, `SegmentExpansion` are all `public` — so a plugin
whose only bespoke part is the reader (the MySQL-binlog case above) stays small.

**Rule of thumb:** the descriptor covers *"another SQL database we poll with `SELECT` and write with
plain statements."* Everything past that line is a plugin.

---

## What the abstraction already gives us

Bounding the work before estimating it:

- **`IDriver` is tiny** — `DriverType`, `ConnectionParameters`, `DefaultPort`, three Kind lists,
  `CreateConnection`, and three catalog methods. Optional behaviour is opt-in via marker interfaces
  (`IConnectionTester`, `IProvisioner`, `IDialectProvider`, `IStatementPreview`, `IPositionCapturing`,
  `ISegmentExpandingReader`). A compiled driver implements a small, well-fenced surface.
- **Capability discovery is engine-neutral and live.** `DriverRegistry.Describe` →
  `GET /api/connections/{name}/capabilities` already drives every reader/cache/writer picker in the
  SPA and every config-validation check. A new driver — B or C — appears in those pickers with no
  SPA change beyond making the *driver-type* field itself open-ended (below).
- **We already run operator-supplied code in-process.** Hooks are arbitrary SQL; scripts are
  arbitrary C# compiled by `ScriptCompiler` into a collectible `AssemblyLoadContext` and executed.
  "A driver is code the operator chose to trust" is not a new trust boundary — it is the one the
  config repo already is.
- **`ScriptCompiler` is a working template for the loader.** Per-unit `AssemblyLoadContext`,
  `MetadataReference`s resolved from already-loaded host assemblies, compiled bytes cached beside the
  state DB. Plugin loading is the same shape without the compile step.

---

## Blockers to clear

### 1. `ConnectionDriverType` (and `StateEngine`) are closed enums (Phases 1 and 6)

`ConnectionDriverType { MsSql, Postgres, DuckDb }` in `DbDataSync.Core` is the driver's identity, and
it is compiled shut. `StateEngine { Sqlite, MsSql, Postgres }` in `DbDataSync.State` is the same
problem one layer down — retired the same way, to a string id, so a `StateDialect` can be registered
rather than added to a `switch`. The driver enum reaches:

- `ConnectionConfig.DriverType` / `ConnectionInput.DriverType` — YAML-serialised, git-committed
- `DriverRegistry`'s dictionary keys and every `Describe`/`Get`/`FindReader` signature
- `ConfigValidation`, `DriverConnectionFactory`, `ChangeSourceResolver`, `ParameterCheck`, and
  ~a dozen other API services
- the SPA: `export type DriverType = 'MsSql' | 'Postgres' | 'DuckDb'` and the connection editor's
  driver picker

**Proposal: replace it with a `string` driver id** (`"MsSql"`, `"Postgres"`, `"DuckDb"`,
`"oracle.generic"`, …). The enum's own comment already says "Extended as new drivers are added" — it
just assumed a recompile. A string is what it always meant.

- YAML migration: existing files already write `driverType: MsSql`; a string round-trips the same
  text, so no rewrite of committed config — only the loader's type changes. Worth a one-time
  validation pass that rejects an unknown id with a clear message naming the `driver install`
  command.
- `DriverRegistry` re-keys on `string`. `Get`/`TryGet`/`Describe` signatures change; the call sites
  are mechanical.
- SPA: `DriverType` becomes `string`; the driver picker is populated from a new
  `GET /api/drivers` endpoint (installed drivers + display name + whether they are built-in) rather
  than a hard-coded union.
- The three built-in drivers keep their current ids exactly, so nothing observable changes for an
  existing deployment.

This is the big, boring diff. Do it first and alone.

### 2. Two composition roots

Drivers are registered in `DbDataSyncHost.cs` (API) *and* `TaskRunner/Program.cs` (worker) — the
worker is a separate `dotnet exec` process. Plugin discovery and loading must be one shared routine
(a new `DbDataSync.Drivers.Loader`, or a static in `Abstractions`) that both roots call, pointed at
the same `<repo>/drivers/` directory. Both processes already know the repo root (the API from
`ApiOptions.RepoRoot`; the worker gets it on the command line).

### 3. `Abstractions` as a public contract (Phase 5 only)

If external assemblies implement `IDriver`, `DbDataSync.Drivers.Abstractions` has to be published to
NuGet and versioned with real semver discipline — a breaking change to `IChangeReader` becomes a
major bump and a compatibility window, not a same-PR refactor. The descriptor sidesteps this entirely: its
only contract is `System.Data.Common.DbProviderFactory`, which the BCL has kept stable for two
decades.

---

## Assembly loading design

Applies to a compiled driver, and to the descriptor's *provider assembly* (still a third-party DLL and its transitive
deps).

- **One `AssemblyLoadContext` per driver**, created by the shared loader, name
  `driver:<id>`. Not collectible for v1 — a driver lives for the process; hot-reload is a non-goal.
- **Shared-contract assemblies resolve to the host's already-loaded copy**, never the plugin's:
  `DbDataSync.Drivers.Abstractions`, `DbDataSync.Core`, `System.Data.Common`, and the framework.
  The ALC's `Load` override returns `null` (→ falls through to the default context) for any assembly
  the default context already has; only genuinely private dependencies (`Oracle.ManagedDataAccess`,
  `MySqlConnector`, …) load from the driver folder. This is the standard
  `AssemblyDependencyResolver` + fallback pattern and the thing that stops "two `IDriver` types that
  aren't the same type" bugs.
- **Native assets**: `AssemblyDependencyResolver.ResolveUnmanagedDllToPath` handles
  `runtimes/<rid>/native/` — Oracle's OCI shim, SQLite's `e_sqlite3`, etc. resolve without extra
  work.
- **Version conflicts with the host** (a driver that wants a newer `Microsoft.Bcl.AsyncInterfaces`
  than the host loads) are surfaced at load time with a diagnostic, not left to fail cryptically at
  first use. The loader logs the driver's dependency closure and any assembly it had to down-bind to
  the host version.

---

## Acquisition — how the package gets onto disk

Three options considered:

- **Runtime NuGet client** (host downloads packages via `NuGet.Protocol` on operator action).
  Rejected for v1: pulls a large dependency and a network requirement into the running host, and
  NuGet's dependency resolution is genuinely hard to reimplement correctly.
- **Build-time meta-package** (operator maintains a project that references extra drivers, produces a
  custom build). Rejected: it *is* a custom build, which is the thing we are trying to avoid.
- **Restore-then-load** (recommended). Each driver has a **manifest** — for a descriptor driver it *is* the
  `driver.yaml`; for a compiled driver a small `driver.json` — listing its id, its direct package references
  with versions, and (D) the assembly and type that implement `IDriver`. `dbdatasync driver install
  <packageId>[ <packageId>…] [--version v] [--source feed]` seeds the manifest, then writes a
  throwaway SDK-style project referencing every package in it, runs `dotnet restore` so NuGet's real
  resolver produces the full transitive closure, and copies the result into
  `<repo>/drivers/<id>/lib/`. `dbdatasync driver sync` re-runs the restore from an existing manifest
  (a fresh deployment, or after editing a version). The running host needs no NuGet client — only the
  `dotnet` muxer `ProcessSupervisor` already depends on.

A driver requiring several packages is the normal case, not an edge one: Oracle ships native-asset
packages beside `Oracle.ManagedDataAccess.Core`, DB2 splits by platform, a compiled driver may bundle
a protocol library. The manifest holds the list; restore handles everything under it.

The `drivers/` directory — manifests and, per Q2, possibly the restored `lib/` — lives **inside the
git-tracked config repo**: an install is a reviewable commit, `uninstall` is a reviewable commit, and
a second deployment pointed at the same repo picks the driver up (Q2: on next start, or after
`driver sync`).

---

## Security and trust

Loading a NuGet package executes its code in the API process. This is consistent with the trust
model the config repo already has — hooks run arbitrary SQL against production, scripts run
arbitrary C# in-process — but it should be explicit:

- Driver install is an **admin action**, gated by the same authz as config writes.
- Nothing is fetched from a bare config value. A driver appears only because someone ran
  `driver install` and committed the result; the CI/review process for the config repo is the
  control point.
- The descriptor and the resolved dependency list are diffable in the Version Control tab, so
  "what got added" is visible.
- Document the residual risk plainly in the install command's output and the docs: a malicious or
  compromised package is RCE. Same sentence the scripting feature needs and does not currently say
  loudly enough.

---

## Type mapping and catalog coverage — the real risk in the descriptor

The generic layer's hard edges are `SqlDialect.ToCanonicalType` / `RenderColumnType` and catalog
introspection. A declarative descriptor has to express:

- **Native → canonical type map** for the engine's type vocabulary. Probably a table in the
  descriptor (`NUMBER(p,s) → decimal`, `VARCHAR2 → string`, `CLOB → text`, …) with a documented
  fallback for anything unlisted. Getting this wrong produces bad `CREATE TABLE` on provisioning and
  wrong segment-bound binding — both loud, not silent, but both annoying.
- **Catalog queries**: `information_schema` is the portable default and covers MySQL, Postgres-alikes,
  SQLite (partially), Firebird. Oracle needs `ALL_TAB_COLUMNS`/`ALL_TABLES`. ODBC has no SQL catalog
  at all — it needs `DbConnection.GetSchema(...)`, a different code path. The descriptor picks one of
  a small set of named catalog strategies rather than carrying raw SQL.
- **Parameter binding by type**: phase 9's point about binding a segment bound as its column's real
  type (so the engine seeks an index instead of converting the column) needs a native-type →
  provider-`DbType` mapping too. Another descriptor table.

Initial scope should be deliberately narrow: `information_schema` engines, watermark + batch +
`DeleteInsert`, a starter type map for the two or three engines we actually test. ODBC and Oracle
catalogs are follow-ons.

---

## What this does not do

- **CDC for an external engine.** Change-data readers are a compiled driver, per engine, and each is
  its own planning question (`change-tracking-oracle.md`, `change-tracking-mysql.md` already exist).
- **A pluggable *state store* backend.** `StateEngine` (SQLite / SqlServer / Postgres, phase 63) is a
  separate closed set. A plugin driver serves replication only; it does not make its engine available
  to hold DbDataSync's own run/log/watermark data.
- **Hot-reload.** A driver change takes effect on process restart.
- **A driver marketplace / auto-update.** Install is explicit and version-pinned.
- **Sandboxing the driver.** It runs with the host's privileges, like hooks and scripts.

---

## Package size — not a lever here

The first draft claimed the loader could shrink the ~150 MB package by making DuckDB a plugin. It
can't: verification and segmenting depend on the embedded DuckDB engine, not just replication, so
it ships regardless. The size is a fixed cost of the built-in engines. If it has to come down, that is a separate exercise on DuckDB's
native assets — per-RID split packages so a Linux container does not carry the macOS `.dylib`,
`<RuntimeIdentifiers>` at pack time, or dropping platforms we do not ship — and none of it touches
this design.

**Update, phase 146**: it came down, without any of the three levers above. DuckDB does still ship to
every real deployment "regardless," exactly as argued — but the package no longer has to carry all five
platforms' native binaries to make that true, because the loader this doc designed was already
RID-aware for the *managed* half (109i) and turned out to need zero new code to also carry the native
half once the packaging exclusion widened to cover it. See
`architecture/implementation/done/phase-146-duckdb-native-asset-exclusion.md`.

---

## Open questions

Each carries a leaning and stays **UNDECIDED** until confirmed. Grep `UNDECIDED` to find what's
still open.

### Q1 — Descriptor and compiled plugin — *resolved: both*

Decided (2026-09-08): ship the YAML descriptor **and** compiled-C# drivers. They serve different
needs — the descriptor for "another SQL database we poll and write with plain statements," a compiled
driver when we need real control (a bespoke reader, branching type logic, an engine-specific write).

The design constraint that falls out: the descriptor must deserialise into the **same
`public GenericDriver`** a compiled driver subclasses, so there is one model with two front ends, not
two parallel implementations. Type maps stay strictly name-keyed; an engine that needs value-aware
mapping (`tinyint(1)`) is the signal to subclass `GenericDriver` for that engine rather than to grow
the YAML schema into a language.

**Still open (minor):** whether the descriptor also allows a `partial` escape hatch — a descriptor
that names a compiled `GenericDriver` subclass for the two methods it overrides and stays declarative
for the rest. Defer until an engine actually wants it.

### Q2 — Do restored driver DLLs go in git?

- **Commit the DLLs:** deployment reproducible from the repo alone; works air-gapped; no restore at
  boot. Large opaque binary diffs, third-party binaries in git.
- **Commit a lockfile only** (package id + version + closure hash), rebuild into a git-ignored cache
  on demand: repo stays clean and diffable, matches the "repo is the source of truth" philosophy.
  Adds a `driver sync` step and a feed dependency for a fresh deployment.

**Resolved 2026-09-13: no.** Lockfile only — the restored `lib/` DLLs are not committed to git.
`dbdatasync provider sync` / `driver sync` (explicit, not silent-on-boot — like `npm ci`) restores them
on a fresh checkout or after an edited version. Air-gapped shops point `install`/`sync --source` at an
internal feed rather than the hybrid vendor-commit option this question left open; that option is
dropped, not merely deferred — 109c already built the plain lockfile path and confirmed it (see its own
"Open questions": "a deployment-policy decision, not an engineering one this phase forces"), and no
fully-offline requirement has actually surfaced to justify the extra commit-the-closure mode.

### Q3 — Contract versioning cadence for `Abstractions` (Phase 5 only)

- **Strict semver + a support window** (e.g. N‑1 major for 6 months): heavy process for a pre‑1.0
  project.
- **A `ContractVersion` integer** on `IDriver`; host loads plugins in `[min, current]` and keeps a
  shim for the last one: pragmatic, common (Roslyn analyzers, MSBuild tasks).
- **No promises:** `Abstractions` versions with the app; a mismatch fails loudly with "rebuild
  against x.y". Cheapest; fine while plugin authors are us or close partners.

**Resolved — built exactly as leaned, in phase 109e.** An integer `IDriver.ContractVersion` (defaults
to 1 via a default interface member on every existing driver, no source change needed), no semver
promises, and a load that fails loudly and is skipped — never crashes the host — when the host can't
support it. See `architecture/implementation/done/phase-109e-compiled-idriver-plugins.md`. Revisit only
if an external plugin ecosystem actually forms.

### Q4 — Worker startup cost

The TaskRunner is spawned per run; loading every installed driver's ALC on each spawn has a latency
cost.

- **Load all installed drivers on every worker start:** simple; fine for 2–3 drivers, maybe not 10.
- **Load only the drivers a run's mappings reference:** the worker knows its replication → mappings
  → connections → driver ids. Bounded cost, a lazy registry to write.

**Resolved 2026-09-13: not now.** No measurement was taken during 109c/109e, and none is being
commissioned to close this out — load-all-eagerly stays the behavior until it's an observed operational
problem, not a hypothetical one. Left unmeasured, and accepted as such.

This also means a related, narrower gap 109e flagged stays accepted rather than fixed: if a compiled
driver depends on a provider (109c) that hasn't yet been resolved into the shared
`AssemblyLoadContext.Default` at the moment the driver's own loader runs, the driver loads a *private*
copy of that provider from its own `lib/` instead of sharing the host's type identity — not observed in
either composition root (providers load before drivers in both today), not exercised by any test. Fixing
it properly means either the eager-loading this question just declined, or teaching a driver's resolver
about providers it should never resolve privately — revisit both together if either becomes real.

### Q5 — Test story

An integration test for a NuGet-loaded driver needs a package to install and an engine to hit.

- **Fixture package + container engine:** publish a tiny fixture provider to a local feed in CI,
  `driver install` it, round-trip against a new (e.g. MySQL) container. Most realistic; real setup
  cost (fixture project, local feed, container, CI minutes).
- **Loader in isolation + `driver install` separately:** pre-built fixture assembly on disk for the
  ALC/shared-contract/version-conflict cases; a separate thinner test that `install` restores a
  known closure. Cheaper; less end-to-end confidence.
- **SQLite as the canary:** `driver install Microsoft.Data.Sqlite`, point a connection at a temp
  `.db`, round-trip. Real maintained provider, real SQL, no container — and doubles as a genuinely
  useful driver.

**Resolved — went further than the leaning.** 109c/109d proved the mechanism against a real MySQL
container via `MySqlConnector` (a package referenced in **no** `.csproj` in the solution) rather than
settling for the SQLite canary alone — the stronger "we genuinely didn't compile this in" confidence
this question raised as optional. 109e's own plugin-loader tests used a fixture published directly via
`dotnet publish` rather than a local NuGet feed, since the restore mechanism itself was already proven
in 109c. See `architecture/implementation/done/phase-109c-the-provider-layer.md` and
`-109d-the-yaml-descriptor.md`.
