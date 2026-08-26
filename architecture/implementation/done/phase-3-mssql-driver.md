# Phase 3 — Driver Abstraction + MSSQL Driver v1

**Status**: Complete (Change Tracking + generic batch reader, staging, MERGE writer — CDC deferred, see Notes)
**Plan reference**: `architecture/implementation-plan.md` § Phase 3

## What was built

**`src/DataSync.Drivers.Abstractions`**: `IDriver`, `IChangeReader`, `IStagingProvider`, `IChangeWriter`,
plus `DriverRegistry` (register/get/`SupportsReader`/`SupportsStagingProvider`/`SupportsWriter`) — the
interfaces from `architecture/detailed-design.md` §3.4, made concrete. Supporting types: `ChangeRow`
(operation + column values, PK-only for deletes), `ReadResult` (lazy `IAsyncEnumerable<ChangeRow>` +
a cursor computed up front), `StagedChangeSet`/`WriteResult`, `TableMetadata`/`ColumnMetadata`.

**`src/DataSync.Drivers.MsSql`**:
- `MsSqlDriver` — connection creation (`SqlConnectionStringBuilder`, credential resolved by the
  caller via `SecretStore` and passed in) and metadata introspection (`sys.databases`/`sys.tables`/
  `sys.columns`, the latter formatting a DDL-ready type spec like `nvarchar(50)` or `decimal(18,2)`
  from raw length/precision/scale — a bare type name alone would silently default `nvarchar` to
  length 1 in a `CREATE TABLE`).
- `MsSqlChangeTrackingReader` (`Kind = "MsSqlChangeTracking"`) — the primary reader. First run with
  no cursor does a full-table load (all rows as Insert); subsequent runs join `CHANGETABLE(CHANGES
  ..., @previousVersion)` back to the base table, checking `CHANGE_TRACKING_MIN_VALID_VERSION` first
  and throwing a clear "full resync required" error if the stored cursor has aged out of the
  retention window. Deletes carry only PK columns (the base-table LEFT JOIN is NULL for a deleted
  row — those NULLs are deliberately not copied into `ChangeRow.Values`, since a real NULL value and
  "row doesn't exist" aren't the same thing).
- `MsSqlBatchReader` (`Kind = "Batch"`) — generic `WHERE watermarkColumn > @previousCursor` fallback
  for tables without Change Tracking. Cannot detect deletes at all (documented limitation, not a bug).
- `MsSqlStagingTableProvider` (`Kind = "MsSqlStagingTable"`) — creates a session-scoped `#temp` table
  shaped from the *target* table's real column types, then streams the change rows into it via
  `SqlBulkCopy`, fed by a custom `ChangeRowDataReader : DbDataReader` adapter so the async row stream
  never has to be materialized into memory.
- `MsSqlMergeWriter` (`Kind = "MsSqlMerge"`) — a single `MERGE` statement from the staging table into
  the target, keyed on the target's real primary key (introspected, not config-supplied), with a
  DELETE branch gated on the staging row's `__Operation` marker.
- `MsSqlSchemaQueries` — the shared `sys.*` catalog queries (columns w/ formatted types, primary key
  columns) used by both metadata introspection and the CT reader/staging/writer's own schema needs.

## How this was verified

A real SQL Server 2022 instance (Docker, `mcr.microsoft.com/mssql/server:2022-latest`) was used for
integration testing during this phase rather than testing against assumptions about Change
Tracking/MERGE/bulk-copy behavior. 12 integration tests in `DataSync.Drivers.MsSql.Tests`
(tagged `[Trait("Category", "Integration")]`, each against a throwaway per-test-class database with
Change Tracking enabled):
- Metadata: connecting via `MsSqlDriver.CreateConnection`, listing databases/tables/columns, and
  confirming column type formatting (`nvarchar(50)`, `decimal(18,2)`) and primary-key detection.
- Change Tracking reader: full load as all-Inserts; a combined insert+update+delete round trip
  correctly classified by operation, with deletes carrying only PK values; a no-op incremental read
  still advancing the cursor.
- Batch reader: missing `watermarkColumn` option throws; full load; incremental returns only rows
  above the previous watermark.
- **End-to-end pipeline** (`MsSqlPipelineTests`): manually wires reader → staging → writer (since
  `DataSync.TaskRunner`, which will do this orchestration for real, doesn't exist until Phase 4) and
  proves a full load followed by an incremental insert+update+delete round trip replicates correctly
  into a separate target table, plus that a no-change incremental run writes zero rows.

7 additional non-integration unit tests (`DriverRegistryTests`) cover the registry against a real
`MsSqlDriver` without needing SQL Server. CI (`.github/workflows/ci.yml`) now runs
`dotnet test --filter "Category!=Integration"`, since standing up a SQL Server service container in
CI is a Phase 7 concern, not this one — these 12 tests currently only run locally against Docker.

## A real bug found and fixed during this phase

The first version of `MsSqlPipelineTests` reused **one connection** for both the source Change
Tracking read and the target `SqlBulkCopy`/MERGE — convenient since the test's source and target
tables happened to live in the same database. This **deadlocked silently** (no exception, no SQL
Server-side blocking visible in `sys.dm_exec_requests` — the .NET process just never returned).
Root cause, confirmed by tracing: `SqlBulkCopy` holds its connection exclusively for the duration of
the copy; the source reader's query is lazily executed *from inside* `SqlBulkCopy.WriteToServerAsync`'s
row-pull loop (since `IChangeReader.ReadChangesAsync` returns a lazy `IAsyncEnumerable`), so it tries
to run a second command on the same connection while the bulk copy is mid-flight. **`MultipleActiveResultSets=True` does not fix this** — MARS interleaves independent batches, but a bulk-copy session isn't an ordinary batch.

Fix: `MsSqlDriver.CreateConnection` now sets `MultipleActiveResultSets = true` regardless (still
needed for other same-connection scenarios), and — more importantly — `IChangeReader`'s XML doc now
states explicitly that **the source connection and the target connection must always be different
instances**, even when source and target are the same physical server. The test was fixed to open
two connections. This is not expected to affect Phase 4: `DataSync.TaskRunner` will naturally open
one connection per `ConnectionConfig` (source and target are separate configs), so it would never
have hit this in real usage — but it's exactly the kind of assumption that's cheap to get wrong
silently, hence documenting it loudly on the interface itself rather than leaving it as tribal
knowledge from this session.

## Decisions made this phase

- **Local `#temp` tables for staging**, not a persistent staging table. They're automatically
  cleaned up when the connection closes (no orphaned-table cleanup logic needed) and are naturally
  scoped correctly. This does mean `IStagingProvider.StageAsync` and the following
  `IChangeWriter.ApplyAsync` call must share one open connection — documented on both interfaces.
- **`ColumnMappings` is required (non-empty) to stage a change set** — v1 doesn't attempt to infer a
  same-name column mapping from the first row's schema. `TableMappingConfig` already has a dedicated
  section for this; requiring it keeps staging table generation simple and fully deterministic
  instead of schema-sniffing an async stream that might produce zero rows.
- **`TrustServerCertificate = true` by default** in `MsSqlDriver.CreateConnection` — needed to
  connect to the self-signed local Docker instance used for this phase's testing. Flagged explicitly
  (in code and here) as needing revisit before any hardened-production deployment guidance ships;
  `ConnectionConfig.Properties` already provides an override path (applied after the default, so a
  connection can turn it back off).
- **CDC reader deferred**, not built this phase. `implementation-plan.md` frames it as "added once
  the Change Tracking path is proven" — that's now true, but CDC's setup (capture jobs, log reader
  agent) is enough additional surface area that bundling it into an already-large phase risked a
  shakier implementation of both. Change Tracking + the generic batch fallback are enough to
  complete Phase 4/5's end-to-end pipeline; CDC can land as a follow-up `IChangeReader` without
  touching anything else, per the driver-registry design in §5 of `detailed-design.md`.
- **Ordered insert/update/delete writer fallback also deferred** alongside CDC, for the same reason —
  `MsSqlMergeWriter` covers the primary path from `detailed-design.md` §3.5.

## Notes / things to revisit later

- No code in `DataSync.Api` or `DataSync.TaskRunner` references these drivers yet — Phase 4 wires
  `DataSync.TaskRunner`'s pipeline to `IDriver`/`IChangeReader`/`IStagingProvider`/`IChangeWriter`
  directly; Phase 5 wires `DataSync.Api`'s metadata-browsing endpoints to `IDriver`'s introspection
  methods.
- CDC reader and the ordered-statements writer are explicit backlog items for a Phase 3 follow-up,
  not silently dropped scope.
- The Docker SQL Server container used for this phase's testing (`datasync-mssql`,
  `localhost:14330`) is left running for continued local development; it isn't part of the committed
  repo or CI.
