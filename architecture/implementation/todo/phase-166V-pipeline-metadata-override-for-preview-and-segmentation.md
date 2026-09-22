# Phase 166V — Extend scripted metadata overrides to the pipeline's remaining live-catalog call sites

**Status**: Design only — not yet built.
**Plan reference**: `architecture/implementation/done/phase-029-scripted-metadata-providers.md` (built
`ScriptSlots.MetadataProvider` — a connection-level script that overrides `IDriver.ListDatabasesAsync`/
`ListTablesAsync`/`ListColumnsAsync` for the SPA's browsing pickers — and named, in its own "Open
questions", the gap this phase closes: *"Overriding the pipeline's `ITableCatalog` needs per-connection
driver components. Worth doing when a driver genuinely has no working catalog (ODBC, JDBC), which is
also when those drivers land."*), `phase-030-scripted-source-queries.md` (reaffirmed the same named seam,
`ITableCatalogProvider`), `phase-165V-jdbc-reader-spike-ikvm-postgres.md` (the motivating driver:
`JdbcCatalog` reuses `InformationSchemaQueries` against Postgres's own `information_schema` — correct for
Postgres, silently wrong or a hard failure for any other JDBC-backed engine the same driver is pointed
at), `architecture/planning/done/additional-database-drivers.md` ("metadata browsing shape... still open
for ODBC/JDBC, which have no fixed shape to assume at all").

## Why this phase is narrower than phase 029's own framing suggested

Phase 029's own phrasing — "the pipeline reads the driver's own catalog" — reads as if the *entire*
pipeline bypasses a bound `metadataProvider` script. Tracing the actual call graph, rather than trusting
that phrase at face value, found otherwise:

**Mapping schema refresh — the thing that actually determines what every reader and writer read and
write, via phase 91's `CachedColumn` cache — already honors a bound script.**
`MappingMetadataService.RefreshAsync` → `MappingColumnReader.ReadAsync` → `IColumnCatalog` (DI-bound to
`MetadataService`) → `ScriptedMetadata`, which resolves the bound script if one exists and falls back to
the driver only if not. A JDBC connection with a working bound script today already gets a *correct*
mapping refresh and browsing experience, for any engine, regardless of what `JdbcCatalog` itself knows
how to query.

Every live (non-cached) `.GetColumnsAsync`/`catalog.GetColumnsAsync` call site in
`src/DbDataSync.Drivers.Generic/*.cs` was checked, without exception:

- `WatermarkReader.DescribeAsync`, `BatchReloadReader.DescribeAsync`, `KeyReconcileReader.DescribeAsync`,
  every writer's `DescribeAsync` (via `TargetShape.LoadAsync`), `BatchInsertStagingProvider.DescribeAsync`,
  `TriggerAuditReader`'s own `DescribeAsync`-only `ResolveColumnsAsync` — all preview-only. The real
  `ReadChangesAsync`/`ApplyAsync` path for every one of these is cache-only (phase 91): `sourceColumns`/
  `targetColumns` parameters or `FromCachedColumns`, no live catalog call at all.
- `BatchReloadReader.ExpandAutoSegmentsAsync`/`KeyReconcileReader.ExpandAutoSegmentsAsync` — a live call,
  but from auto-segmentation's own setup-time column lookup, not the row-moving path.
- `RunExecutor.CacheProvisionedTargetColumnsAsync` — a live call, but an already-accepted, separately
  documented exemption ("a live introspection inside a run, which phase 91 otherwise has none of...
  The exemption is provisioning's, not the pipeline's"), target-side only, right after provisioning. Not
  in scope here.

**So the real, remaining gap is narrower and more precisely named**: an operator's bound
`metadataProvider` script already makes mapping refresh and browsing correct for a JDBC connection whose
driver-level catalog doesn't fit that connection's real engine — but the SQL **preview screen** and
**auto-segmentation's column lookup** still ask the driver's own catalog directly, ignoring that same
script. For phase 165V's JDBC driver specifically: bind a script, and replication itself, and the picker,
are both already correct for a non-Postgres JDBC-backed connection today — but the preview screen would
still show whatever (wrong, or failing) answer `JdbcCatalog`'s Postgres-shaped `information_schema` query
gives, and auto-segmentation would use that same wrong answer to plan segments.

This phase closes exactly that gap. It is **not** a generic, cross-vendor JDBC catalog (still an
operator's job, per engine, via a bound script — out of scope, see below) and **not** a rewrite of how
drivers are registered (phase 029's own assumed-necessary "per-connection driver components" turns out
not to be the only way to get there — see the mechanism below).

## What this phase will build

### The mechanism: a `DbConnection`-keyed catalog override, not a per-connection driver instance

Every live catalog call site already receives the specific `DbConnection` it queries
(`catalog.GetColumnsAsync(request.Connection, schema, table, ct)` in every `DescribeAsync`; the same
shape in `ExpandAutoSegmentsAsync`). Phase 029 assumed the only way to vary the catalog per connection
was constructing a whole new set of driver components (readers, writers, staging providers) per
`ConnectionConfig` instead of once per `DriverType` — a real rewrite of driver registration. That is not
the only way: since the connection instance is already threaded through every call site, an override can
be looked up *by that instance* at read time, with no change to how drivers are registered or how
`IChangeReader`/`IChangeWriter`/`IStagingProvider` are constructed.

Concretely:

- A new small type in `DbDataSync.Drivers.Generic` (name TBD — `PipelineMetadataOverrides`, say):
  registers and looks up an override keyed by the specific `DbConnection` instance, the same
  `ConditionalWeakTable<DbConnection, T>` idiom `ConnectionTimeouts` already established for stamping a
  resolved value onto a connection (`src/DbDataSync.Core/Sql/ConnectionTimeouts.cs`) — dies with the
  connection, no explicit cleanup required.
- Every driver wraps its own singleton catalog once, at construction, in a small decorator implementing
  `ITableCatalog`: `GetColumnsAsync(connection, schema, table, ct)` checks the registry for that specific
  `connection` first, falling through to the wrapped driver catalog when nothing is registered. No
  existing reader/writer/staging-provider constructor changes — they already take an `ITableCatalog`;
  only what gets passed to them changes, one line per compiled driver (`PostgresCatalog.Instance` wrapped
  instead of passed bare, identically for MySql/MsSql/Oracle/Jdbc).
- At the layer that already resolves `ScriptedMetadata` — `PreviewService`/`ReconcileService`/
  `BulkLoadService` (`DbDataSync.Api`) and `RunExecutor` (`DbDataSync.TaskRunner`), both of which already
  reference `DbDataSync.Scripting` (confirmed: both csproj files carry the reference already) — before
  invoking `DescribeAsync`/`ExpandAutoSegmentsAsync` on an open connection, register an `ITableCatalog`
  adapter around the bound `IMetadataProvider` script (if one is bound) for that specific `DbConnection`.

### What does NOT need to change

- `IChangeReader`/`IChangeWriter`/`IStagingProvider`/`ITableCatalog` interface shapes — no signature
  changes anywhere.
- How drivers are registered, or the fact that `Readers`/`Writers`/`StagingProviders` are built once per
  `IDriver` instance — phase 029's assumed rewrite does not happen.
- Mapping refresh, browsing, or any cache-only read/write path — already correct today, untouched here.
- `RunExecutor.CacheProvisionedTargetColumnsAsync`'s existing provisioning-time exemption — a separate,
  already-accepted design, not folded into this phase.

## What this phase does not build

- A generic, cross-vendor JDBC catalog (auto-detecting catalog/schema semantics via
  `DatabaseMetaData.getCatalogTerm()`/`supportsCatalogsInDataManipulation()`/`getSchemaTerm()`, or
  similar heuristics) — still explicitly an operator's job via a bound `metadataProvider` script per
  non-Postgres JDBC connection. This phase makes that script's reach *complete* (covering preview and
  auto-segmentation too, not just browsing and refresh), not unnecessary.
- Any change to `JdbcCatalog`/`JdbcDialect`'s own Postgres-scoped implementation from phase 165V.
- Any new UI affordance — an operator watching the preview screen simply sees correct output when a
  script is bound; there is nothing new to surface, matching how `ScriptedMetadata`'s existing browsing
  override is already silent and transparent.

## Open questions

1. **Exact registry API and lifetime.** A `ConditionalWeakTable<DbConnection, ITableCatalog>` is the
   obvious shape, but whether registration is push-based (caller registers an already-built adapter, then
   the connection's own disposal drops it) or resolved lazily on first lookup (caller registers a
   producer, since building a `MetadataContext` needs the driver, the dialect and the script's resolved
   parameters, not just the connection — see `ScriptedMetadata.ContextFor`) needs to be settled against
   `ScriptedMetadata`'s actual signatures, not assumed here.
2. **Does `ExpandAutoSegmentsAsync` really run somewhere that already has `ScriptedMetadata` in scope?**
   This phase traced `RunExecutor` as *a* caller in that vicinity, not exhaustively every call site of
   `ExpandAutoSegmentsAsync` itself — confirm the actual call site before implementing.
3. **Does every compiled driver's `Readers`/`Writers`/`StagingProviders` construction site need touching,
   or can the decorator be applied once, centrally**, wherever `IDriver` instances are composed into the
   pipeline (`BuiltInDrivers`, `DriverConnectionFactory`, or wherever `IDriver.Readers` is actually
   consumed) — a single wrap is less error-prone than five-plus parallel edits that could drift as new
   drivers are added. Not decided here.
4. **Does the descriptor-driven `GenericDriver` (phase 109d, YAML-configured drivers) need this too** —
   presumably yes, for the identical reason JDBC does (an operator-declared driver has no guarantee
   `information_schema` exists either), but not traced in this doc.

## How to verify when built

- A JDBC connection (phase 165V) with a bound `metadataProvider` script that deliberately answers
  something `JdbcCatalog`'s own Postgres-scoped query would not: the preview endpoint's declared
  parameter type and auto-segmentation's column resolution both reflect the script's answer, not
  `JdbcCatalog`'s.
- The same connection with no script bound: preview/auto-segmentation fall through to `JdbcCatalog`
  exactly as today — no regression for the common case (every non-JDBC engine, and Postgres-via-JDBC).
- Full existing suite green — this phase changes nothing about the cache-only run path, so no existing
  reader/writer/staging-provider integration test should need to change.
