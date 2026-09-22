# JDBC metadata — `DatabaseMetaData` by default, three escape hatches, no pipeline workaround needed

**Status: design settled in planning — not yet built.**

**Plan reference**: `architecture/planning/todo/jdbc-driver-support.md` (this doc's own still-open
"metadata browsing shape" risk), `architecture/implementation/done/phase-029-scripted-metadata-providers.md`
(built the `metadataProvider` script mechanism this doc leans on), `architecture/implementation/done/phase-165V-jdbc-reader-spike-ikvm-postgres.md`
(the real, built JDBC driver this doc changes), `architecture/implementation/todo/phase-166V-pipeline-metadata-override-for-preview-and-segmentation.md`
(**superseded** — see "Replacing phase 166V" below; its proposed mechanism should not be built),
`architecture/implementation/done/phase-109d-the-yaml-descriptor.md` (the `driver.yaml` format this doc
extends), `architecture/planning/done/additional-database-drivers.md`, `architecture/planning/done/change-tracking-odbc-jdbc.md`.

**Note on scope**: phase 165V and 166V exist only on `dev`/`main` as of 2026-09-21 (commit `222e216`) —
not yet on this `planning` branch. Everything below about the current code is read from that commit
directly, not from this branch's working tree. `GenericDriverSpec`/`driver.yaml` (phase 109d) are on this
branch already.

## The decision

Four mechanisms cover JDBC — and generic-descriptor — metadata variance across vendors, without
DbDataSync building or maintaining a bespoke catalog per engine. Two already exist; one changes; one is
new:

1. **Default: `java.sql.DatabaseMetaData`, not a query.** JDBC's native catalog issues no SQL by default
   — it calls the standard JDBC metadata API (`getCatalogs`/`getSchemas`/`getTables`/`getColumns`/
   `getPrimaryKeys`), which is part of the JDBC spec itself and works across engines without assuming
   `information_schema` exists. This **replaces** phase 165V's `JdbcCatalog`, which currently reuses
   `InformationSchemaQueries` — correct for Postgres, by that class's own doc comment, and nothing else.
2. **Escape hatch — a literal query, from `driver.yaml`.** New. `GenericDriverSpec`'s descriptor format
   gets an optional query-based catalog strategy, for an operator whose engine's `DatabaseMetaData` answer
   doesn't fit DbDataSync's database/schema/table model cleanly. Doesn't exist today — see below.
3. **Escape hatch — per-column target-type override.** Already built: `ColumnMapping.TargetType`. "Null
   is the normal case — the type is inferred... writing that inference into config would freeze today's
   answer" (its own doc comment). No new work.
4. **Escape hatch — the `metadataProvider` script override.** Already built: phase 29's
   `IMetadataProvider`. Strengthened here by making the raw native connection handle reachable to a
   script, rather than DbDataSync designing and maintaining a curated `DatabaseMetaData`-wrapper contract
   — see "Plugins get raw references, not a curated API" below.

(2) and (4) are for genuinely different failure modes: (2) is for an engine whose whole `DatabaseMetaData`
shape doesn't map cleanly (fixed once, in the connection's own driver config); (4) is for anything a
one-off query can't express, or logic beyond a single `SELECT` (fixed in C#, with the driver's own answer
available to filter or ignore). Both stay available; neither replaces the other.

## Plugins get raw references, not a curated API

The earlier draft of this doc framed the choice as "build a `DatabaseMetaData`-shaped wrapper API for
scripts (option A)" vs. "leave every script writing raw SQL against its own vendor's system catalog
(option B)." That framing was wrong to pose as binary — there's a third, simpler shape that gives (4)
everything (A) would have, without DbDataSync designing or maintaining an API surface at all: **make the
real JDBC objects public on the connection, under their own names, rather than hiding them behind an
`internal` accessor.**

`MetadataContext.Connection` is already the live `DbConnection` — for a JDBC connection, that's a
`JdbcConnection` instance. Today that class is itself `internal` to `DbDataSync.Drivers.Jdbc`, and its one
escape hatch, `internal java.sql.Connection Underlying`, is `internal` too — so a `metadataProvider`
script, compiled in a different assembly, cannot even name the type to pattern-match against, let alone
reach the field. Both need to change, and both are decided, not open:

- **`JdbcConnection` becomes a public class.** There is no reason for a script to be unable to name the
  type it is holding.
- **Two public members, named after the literal Java type each one returns:**
  - `JavaSqlDriver` — the underlying `java.sql.Driver`. `JdbcConnection` retains the same reference
    `JdbcProviderFactory.GetJdbcConnection` already resolves at `Open()` time (that factory's own property
    for it is separately named `JdbcDriver` — see the naming note below for why this one isn't).
  - `JavaSqlConnection` — the underlying `java.sql.Connection` (replacing `Underlying`).

A script compiled with a reference to `DbDataSync.Drivers.Jdbc` can then write:

```csharp
if (context.Connection is JdbcConnection jdbc)
{
    var meta = jdbc.JavaSqlConnection.getMetaData();
    // meta.getSchemas(), meta.getColumns(...), etc. — the real, standard JDBC API, no DbDataSync wrapper.
    var majorVersion = jdbc.JavaSqlDriver.getMajorVersion(); // the driver itself, also real and reachable.
}
```

**Naming note.** `JdbcDriver` — the obvious first choice for the `java.sql.Driver` property, mirroring
`JdbcProviderFactory`'s own property of that name — was rejected for it here: `DbDataSync.Drivers.Jdbc`
(a different namespace, `.Imported` for the connection) is also the name of the compiled `IDriver`
implementation from phase 165V, and `JdbcProviderFactory` already uses the name for something else again.
Three different things named `JdbcDriver` in the same driver project is confusing even though none of it
collides at compile time. `JavaSqlDriver`/`JavaSqlConnection` name each property after the actual Java
package and type it returns (`java.sql.Driver`, `java.sql.Connection`) instead, which reads unambiguously
next to `DbDataSync.Drivers.Jdbc.JdbcDriver` and `JdbcProviderFactory.JdbcDriver` rather than beside them.

This is `x is Type y` plus `y`'s own public members — nothing DbDataSync has to design, version, or keep
in sync with the JDBC spec. It also resolves what were open questions 2 and 3 in the earlier draft: there
is no new contract member to place, because `MetadataContext.Connection`'s existing shape is already
enough once the concrete type behind it is public; and any driver reusing `JdbcConnection` under the hood
— a descriptor-driven `GenericDriver` included — gets the same reach for free, with no separate design.

## Phase 166V: same goal, no new mechanism

Phase 166V's goal is real: make Preview and auto-segmentation honor a bound `metadataProvider` script
instead of silently asking the driver's own native catalog directly. Its proposed *mechanism* — a
`DbConnection`-keyed `ConditionalWeakTable` override, decorating every compiled driver's `ITableCatalog`
— should not be built. Neither call site needs it, for two different reasons below. An earlier draft of
this doc recommended dropping 166V entirely, on the theory that both call sites had a cached substitute
sitting unused. **That conclusion was wrong for half of it** — checked against
`TargetShape.LoadAsync` (the writer-side counterpart of the readers' own `DescribeAsync`, not checked
before that draft was written), whose own doc comment states, deliberately: *"`LoadAsync` asks the live
catalog and stays live for preview..., which shows an operator today's real table. `FromCachedColumns`
reads the mapping's cache instead and is what every writer's actual `ApplyAsync` uses... Folding them
into one method... would have made the one thing this phase is not allowed to do — a live fallback — one
`if` away from creeping back in."* That's a real, cited, deliberate reason for preview to stay live, not
an oversight — and there is no principled reason the source-side readers' own `DescribeAsync` would be
live by accident while the target side is live on purpose for the identical screen. The two should be
treated the same until shown otherwise.

**So two different fixes, not one:**

- **`ExpandAutoSegmentsAsync`'s `NativeType` lookup** — unaffected by this correction. It's reached from
  the real run path (`RunExecutor.ResolveSegmentsAsync`), not only from Preview, so it never gets the
  "preview shows live truth" justification `TargetShape.LoadAsync` states for itself. `RunExecutor`
  already has `mapping.SourceColumns` in scope at that exact call site and doesn't pass it through. Thread
  it in, matching `ReadChangesAsync`'s own `sourceColumns.RequireAll(...)` shape (fail loudly, "column not
  found," rather than fall back live) — this part of phase 166V's premise goes away, since there's no live
  catalog call left here to route through a script. (`GetRangeAsync`'s `SELECT MIN(col), MAX(col)` is
  still a data query, still unrelated to any of this, still stays live regardless.)
- **`DescribeAsync`** (`BatchReloadReader`'s, `WatermarkReader`'s, and `TargetShape.LoadAsync` via every
  writer's) — **stays live, deliberately, on both sides.** But it currently reaches the wrong live
  answer: it calls `catalog.GetColumnsAsync(...)` directly — the reader's own `ITableCatalog`, baked into
  the constructor once per *driver type* and shared across every connection of that type — which is a
  different, separate path from the one browsing and refresh already use
  (`MetadataService`/`ScriptedMetadata`, which checks for a bound `metadataProvider` script first).
  Preview was simply never wired to the path that already exists for this.

**The fix is not phase 166V's `ConditionalWeakTable`/decorator mechanism.** That machinery exists to let
something *outside* the reader override an `ITableCatalog` reference that's fixed at construction time,
without changing how readers are built. But there's a plainer option, and it's simpler than what an
earlier draft of this doc, and phase 166V itself, both proposed: **`PreviewService` already has everything
`ScriptedMetadata` needs** — the open connection, the driver, the dialect, the schema/table — at the exact
point it builds each `PreviewRequest`. So: `PreviewService` calls `ScriptedMetadata.ListColumnsAsync(...)`
itself (the same call `MetadataService`'s browsing path already makes), once per `DescribeAsync`, and
passes the result on `PreviewRequest` as a plain field — the same "resolve it where the context already
is, hand it down as data" shape every other call site in this pipeline uses (`ReadChangesAsync`'s
`sourceColumns` parameter, phase 91's whole pattern). `DescribeAsync` reads that field instead of calling
`catalog.GetColumnsAsync` itself. No registry, no per-`DbConnection` lookup, no decorator wrapping every
compiled driver's catalog. The live call still happens — Preview is still live, on purpose — it just
happens in `PreviewService`, through the path that already checks for a script, instead of inside the
reader, through the path that doesn't.

`ExpandAutoSegmentsAsync`'s equivalent fix is the cache (above), not this — it's on the real run path,
where phase 91's rule is no live call at all, script-aware or not.

This also answers phase 166V's own open questions 1 and 3 for free: there's no registration lifetime to
design (question 1), because nothing is registered — `PreviewRequest` just carries a value; and no
decision needed about touching every compiled driver's construction site (question 3), because no
driver's `ITableCatalog` wiring changes at all. **Phase 166V's proposed mechanism should not be built.**
Its goal — route Preview through the bound script — is still correct and still needed; its means was
more machinery than the problem requires.

## What already exists (phase 29) — unchanged by this doc

Two consumers of column metadata, deliberately kept separate:

| | who asks | how |
| --- | --- | --- |
| browsing | the SPA's cascading connection → database → table → column pickers | `MetadataService` → `ScriptedMetadata` → bound script, or `IDriver`'s three methods if none is bound |
| the pipeline | staging, segments, writers | `ReadChangesAsync`/`ApplyAsync` read `sourceColumns`/`targetColumns` cache only (phase 91) — never live, and never will, by design. `DescribeAsync`/`ExpandAutoSegmentsAsync` are the two deliberate exceptions; see "Phase 166V: same goal, no new mechanism" for which of those stay live and why |

The contract:

```csharp
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext context, CancellationToken ct);
    Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext context, string database, CancellationToken ct);
    Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        MetadataContext context, string database, string schema, string table, CancellationToken ct);
}

public sealed record MetadataContext(
    DbConnection Connection,
    IScriptDialect Dialect,
    ScriptParameters Parameters,
    Func<CancellationToken, Task<IReadOnlyList<string>>> DriverDatabases,
    Func<string, CancellationToken, Task<IReadOnlyList<TableMetadata>>> DriverTables,
    Func<string, string, string, CancellationToken, Task<IReadOnlyList<ColumnMetadata>>> DriverColumns);
```

`Connection` is the live, open `DbConnection` — the one thing a script gets that nothing else does. Binds
**connection-level only**; falls straight through to `driver.ListDatabasesAsync`/etc. at zero cost when
nothing is bound.

## Phase 165V's JDBC driver, as built — and what changes here

- `JdbcConnection` (`Imported/`) is currently an `internal` class with one `internal java.sql.Connection
  Underlying` accessor — see "Plugins get raw references" above for the fix: the class becomes public,
  `Underlying` is renamed `JavaSqlConnection`, and a new public `JavaSqlDriver` property (the
  `java.sql.Driver`, already resolved at `Open()` time via `JdbcProviderFactory.JdbcDriver`) is added
  alongside it. Every internal call site that currently reads `.Underlying` (`JdbcCommand`'s
  `_connection.Underlying.createStatement()`/`prepareStatement(...)`) is renamed with it — a mechanical
  rename, nothing about the shape changes.
- `JdbcCatalog` currently reuses `InformationSchemaQueries`, unmodified, run as plain SQL over the JDBC
  connection — scoped, by its own doc comment, to "exactly what this phase's one Postgres test table
  needs." **This is what mechanism (1) above replaces**: `JdbcCatalog` should call `DatabaseMetaData`
  directly instead.
- `JdbcDriver.ListDatabasesAsync`/`ListTablesAsync`/`ListColumnsAsync` (the native `IDriver` fallback used
  when no script is bound) currently forward straight to `JdbcCatalog`. Once `JdbcCatalog` is
  `DatabaseMetaData`-based, this fallback stops being Postgres-specific by construction — it becomes "the
  standard JDBC answer," which may still be an imperfect fit for a given vendor's catalog/schema
  semantics, but is never simply wrong the way running Postgres's `information_schema` SQL against Oracle
  would be. The remaining imperfection is exactly what mechanisms (2) and (4) exist to fix per connection.
- `JdbcDialect.ToCanonicalType` parses native type-name strings scoped to pg_catalog spellings today
  (`"int4"`, `"varchar"`, …), as one hardcoded, compiled dialect. **Decided in "Decided in this round"
  below: this goes away.** A JDBC-backed engine becomes a `GenericDriverSpec`/`driver.yaml` descriptor,
  same as every other descriptor-driven engine, with its own `typeMap` mapping that vendor's
  `DatabaseMetaData` `TYPE_NAME` spellings — not a single compiled `JdbcDialect` trying to cover every
  vendor at once. Phase 165V's hand-written `JdbcDriver : IDriver` was a spike's shape, not the shipped
  one.

## What we see in JDBC

`DatabaseMetaData` — `getCatalogs()`, `getSchemas()`, `getTables()`, `getColumns()`, `getPrimaryKeys()` —
is standard and works across engines, but its catalog/schema *shape* still varies by vendor (Oracle's
"database" is a service/schema; Postgres separates database from schema; MySQL conflates them). That
variance doesn't disappear by switching the default from a query to this API — it's inherent to JDBC
itself. What changes is that DbDataSync no longer has to resolve it centrally: mechanism (1) gives a
reasonable default for the common case, and mechanisms (2) and (4) are the per-connection fixes for
whichever vendor doesn't fit it.

Worth keeping two different "native type" questions distinct, since it's easy to conflate them:

- **Design-time / browsing.** `DatabaseMetaData.getColumns()`'s `TYPE_NAME` (the vendor's own type-name
  string) plus `DATA_TYPE` (a `java.sql.Types` int) — no live row read needed. This is what `JdbcCatalog`
  reads under mechanism (1), and what a `metadataProvider` script (mechanism 4) would produce as
  `ColumnMetadata.NativeType` if it overrides browsing for one connection.
- **Value-shape / read-time.** `ResultSetMetaData.getColumnType()` (what `JdbcDataReader` already uses)
  vs. `getColumnClassName()` (the actual native Java class a bound read returns — a stronger,
  driver-specific signal, unused today). This is the reader's own type-fidelity question, separate from
  browsing, and out of this doc's scope.

## Decided in this round

- **JDBC's type-name parsing uses `driver.yaml`'s `typeMap`, not a hardcoded `JdbcDialect`.** Rather than
  one compiled `JdbcDialect.ToCanonicalType` C# switch trying to cover every vendor's spellings (today it
  only covers Postgres's), a JDBC-backed engine gets a descriptor — the same `GenericDriverSpec`/
  `driver.yaml` shape every other descriptor-driven engine already uses, with its own `typeMap` section
  mapping that vendor's `DatabaseMetaData` `TYPE_NAME` spellings to canonical types. This settles
  `jdbc-driver-support.md`'s own open question 1 ("is a JDBC engine a `GenericDriverSpec`, or its own
  `JdbcDriverSpec`?"): it's a `GenericDriverSpec`, not a hand-written compiled `IDriver` — phase 165V's
  current shape was a spike, not the shipped shape. One consequence worth naming: a `GenericDriverSpec`
  is authored per vendor (`postgres-via-jdbc.driver.yaml`, `oracle-via-jdbc.driver.yaml`, …), same as
  `mysql.generic.driver.yaml` today, rather than one "Jdbc" driver type serving every URL. That's a real
  shift from phase 165V's shape, not a detail.
- **`driver.yaml`'s query-override is two properties, not a new abstraction.** `dialect.catalog` gets a
  third value (`query`, alongside `informationSchema` and a new `databaseMetaData`), and a
  `metadataQueries: { tableQuery: "...", columnQuery: "..." }` block carries the operator's own SQL when
  `catalog: query` is set. `GenericDriverSpec.Catalog` still needs to stop being the concrete
  `InformationSchemaQueries` type (it has to hold whichever of the three strategies the YAML picked), but
  the YAML surface itself is exactly this, not a framework.
- **Phase 166V's mechanism is dropped; its goal is met by `PreviewService` calling `ScriptedMetadata`
  directly and passing the result down `PreviewRequest`.** See above. No registry, no decorator.

## The query-override's row contract

Two record types, matched to the query's result columns by name (case-insensitive), not position — so an
operator's `SELECT` can alias, reorder, or include extra ignored columns:

```csharp
/// <summary>One row of a `driver.yaml` `metadataQueries.tableQuery` result, matched by column name.</summary>
public sealed record QueryTableRow(
    string Schema,   // "table_schema" — required
    string Table);   // "table_name"   — required

/// <summary>One row of a `driver.yaml` `metadataQueries.columnQuery` result, matched by column name.</summary>
public sealed record QueryColumnRow(
    string ColumnName,              // "column_name"              — required
    string DataType,                // "data_type"                — required
    int? CharacterMaximumLength,    // "character_maximum_length" — optional, null = no length bound
    int? NumericPrecision,          // "numeric_precision"        — optional, null = not numeric / unspecified
    int? NumericScale,              // "numeric_scale"            — optional, null = not numeric / unspecified
    string? IsNullable,             // "is_nullable"               — optional, "YES"/"NO", null → treated as "YES"
    bool? IsPrimaryKey,             // "is_primary_key"           — optional, null → false
    bool? IsIdentity);              // "is_identity"              — optional, null → false
```

Column names deliberately match `information_schema`'s own naming for `column_name`/`data_type`/the three
numeric columns/`is_nullable` — the same `SELECT` list `InformationSchemaQueries.GetColumnsAsync` already
runs — so an operator adapting a vendor's near-`information_schema` view (the common case) reuses names
they already recognize. `is_primary_key`/`is_identity` have no `information_schema` precedent (primary
keys need a separate join there), so they're new names chosen to read the same way.

**Required fields are exactly the two `ColumnMetadata`/`TableMetadata` can't function without** — a
column with no name or type isn't a column; a table with no schema or name isn't addressable. Every other
field's default is not a new decision, it's the one the codebase already made where an equivalent case
exists:

- `CharacterMaximumLength`/`NumericPrecision`/`NumericScale` — already nullable in
  `InformationSchemaQueries` today (`Nullable(reader, ordinal)`); same rule here.
- `IsNullable` — missing or `NULL` defaults to `"YES"`. Wrongly assuming nullable costs an unnecessary
  null-check or an over-permissive target; wrongly assuming `NOT NULL` rejects a real value the source can
  actually produce. The safe default direction is toward nullable.
- `IsPrimaryKey`/`IsIdentity` — missing or `NULL` defaults to `false`. Same default
  `InformationSchemaQueries.GetColumnsAsync` already hardcodes for `IsIdentity` today ("standard but not
  universally populated... left false here"), now also offered for `IsPrimaryKey` when a query doesn't
  compute it.

**Missing-column handling.** Each column is looked up once by name (`reader.GetOrdinal`, case-insensitive),
not re-checked per row. `column_name`/`data_type`/`table_schema`/`table_name` absent from the result set at
all → fails immediately when the query first runs against the connection, naming the missing column and
which query it came from — a configuration error, not a per-row failure. Any optional column absent from
the result set → every row gets its default, no per-row check. Present but `NULL` on a given row → same
default, applied per row.

**`DataType` assembly reuses existing logic.** `ColumnMetadata.NativeType` is one formatted string
(`"varchar(50)"`, `"numeric(10,2)"`), built today by `InformationSchemaQueries`'s private
`FormatType(dataType, maxLength, precision, scale)`. The query-based catalog strategy calls that same
method (widened from `private` to `internal`) rather than reimplementing type-string formatting.

## What's still open

1. **Does `DatabaseMetaData.getColumns()`'s `TYPE_NAME` actually agree with what a real read's
   `ResultSetMetaData` produces, per engine?** Two different JDBC calls answering related but distinct
   questions. Phase 165V's own Findings 1–2 are the concrete precedent for "assumed identical, only a real
   run-time comparison caught the difference." Doesn't block implementation — belongs in documentation for
   whoever authors a JDBC-backed `driver.yaml`'s `typeMap`, not an API design question.
