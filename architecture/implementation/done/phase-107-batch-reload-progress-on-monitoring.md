# Phase 107 — batch reload progress on the Monitoring screen

**Status**: Done.
**Plan reference**: no `planning/` doc — a plan-mode session, transcribed below. (Numbered 107
assuming phase 106, "configurable change-processing parallelism", lands first; the two are
independent branches.)

## Why

A backfill launched from the UI enqueues **one independently-scheduled run per segment**
(`BackfillService.EnqueueAsync` → `WorkQueueStore.Enqueue`, one `RunKind.Backfill` row in `WorkQueue`
+ `TaskRuns` each). Each segment run goes `Queued → Running → Succeeded/Failed` and writes its final
`RowsRead`/`RowsWritten` once, at `CompleteRun`. Nothing tied a backfill's segments together, nothing
tracked progress, and the Monitoring "Current Status" tab (`MonitoringPanel.tsx`) showed only
reader-lag cards — `MetricsCard` deliberately excludes backfills. An operator kicking off a large
reload had nowhere to watch it except the raw per-segment rows in Run History.

Goal: a card on Monitoring → Current Status showing a backfill's **rows copied** against a
metadata-estimated **total**, updating as segments complete. No progress bar. The total comes from
engine catalog statistics — `sys.partitions`, `pg_class.reltuples` — never `SELECT COUNT(*)`.

Scope confirmed with the user:
- **Active batch only** on Monitoring (kept ~5 min after completion so the final numbers are seen). A
  dedicated **Batch Load History** screen is future work — the `BackfillBatches` table and the
  `GET .../backfills?limit=` endpoint are built to support it, no UI for it here.
- **Per-segment granularity.** "Rows copied" is the sum of *completed* segments' written rows — a
  single Full-segment reload shows 0 until it finishes, then jumps. Live mid-run counting is future
  work.
- **Filtered sources: whole-table estimate with a caveat.** The estimate is still computed when the
  source table resolves, even with a mapping row `Filter`; the card shows `≈` and a "ignores the
  mapping's row filter" line. Only a query source or a driver with no catalog gives "Unknown".

## What this phase built

### Row-estimate capability — `ITableRowEstimator`
New opt-in interface (`src/DbDataSync.Drivers.Abstractions/ITableRowEstimator.cs`), same shape as
`IConnectionTester` / `ISegmentExpandingReader` — callers ask `driver is ITableRowEstimator`.
- **`MsSqlDriver`** — `SUM(p.rows)` over `sys.partitions` for `index_id IN (0, 1)`, joined to
  `sys.tables`/`sys.schemas` by name so nothing is string-built into `OBJECT_ID`. No permission
  beyond seeing the table.
- **`PostgresDriver`** — `pg_class.reltuples::bigint`; `-1` (never analysed, PG14+) comes back as
  null, not as a count.
- DuckDB deliberately not implemented — it is only ever a query source or a target here, never a
  table-backed backfill source, so `source.Table` is empty and the estimate is skipped upstream.

### State — `BackfillBatches` + `TaskRuns.BackfillBatchId`
One migration template appended to `Migrations.cs`. `BackfillBatches` holds what a segment run
doesn't carry — the **planned** segment count (not `COUNT(RunId)`, which undercounts when an
equivalent segment was already in flight) and one `EstimatedRows` + `EstimateCaveat`. `TaskRuns`
gains a nullable `BackfillBatchId` (indexed), written by `WorkQueueStore.Enqueue` (new optional
`backfillBatchId` parameter) into the `TaskRuns` row it already inserts.

`BackfillBatchStore` (`src/DbDataSync.State/`) mirrors `RunMetricsStore`: `CreateBatch`, and
`GetRecentBackfills(taskName, limit)` which `LEFT JOIN`s `TaskRuns` and rolls the segments up —
`SegmentsSucceeded/Failed/Running`, `SUM(RowsWritten)` as `RowsCopied`, `MIN(StartedAtUtc)`,
`MAX(EndedAtUtc)`. `BackfillBatchProgress.State` derives `Running` / `Completed` /
`CompletedWithFailures` from the counts.

### API
- `BackfillService.EnqueueAsync` mints one `batchId` per call, computes the estimate best-effort in a
  new `EstimateRowsAsync` (opens the source connection, `driver is ITableRowEstimator`, `try/catch` →
  `(null, null)` on any failure; caveat `"ignores row filter"` when the mapping filters and a number
  came back), calls `batchStore.CreateBatch`, and threads `batchId` onto every `Enqueue`.
- `GET /api/replications/{name}/backfills?limit=5` on `RunsController` (`limit` clamped 1–20).
- `BackfillBatchStore` registered in DI next to `RunMetricsStore`.

### Frontend
- `types.ts` — `BackfillBatchProgress` + `BackfillState`. `client.ts` — `api.replications.backfills`.
  `hooks.ts` — `useRecentBackfills`, polling every 2s while a batch is `Running`, else
  `MONITORING_REFRESH_MS`.
- `BackfillProgressCard.tsx` — new, at the top of `MonitoringPanel`'s fragment (above the lag card).
  Renders the newest batch when `Running` or finished within 5 min, otherwise nothing. `Figure`
  tiles: **Rows copied**, **Estimated total** (`≈ n` or "Unknown"), **Segments** (`n / total`, `· k
  failed`), **Started**. `data-backfill-state`, `data-testid`s, and a `.hint` per the "null is not
  zero" house rule.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- **New tests**:
  - `BackfillBatchStoreTests` (State) — roll-up over a mix of segment statuses; `SegmentCount` is the
    planned count not the run count; state derivation; newest-first + limit; replication scoping; the
    real `Enqueue` path stamps the id.
  - `CrossEngineStateTests` / `StateDatabaseTests` — `BackfillBatches` created on all three engines.
  - `MsSqlDriverMetadataTests.EstimateRowCountAsync_…` — reflects rows present, null for a missing
    table (real SQL Server).
  - `PostgresRowEstimateTests` — null before `ANALYZE`, a plausible count after, null for a missing
    table (real PostgreSQL).
  - `BackfillIntegrationTests.Backfills_RollsUpTheSegmentsAndCarriesACatalogEstimate` — end to end
    through real HTTP + a spawned worker + real SQL Server: batch carries `segmentCount` and the
    `sys.partitions` estimate mid-run, and `state: Completed` with `rowsCopied` == the row count once
    the segments finish.
  - `backfill-progress.spec.ts` (Playwright) — the card's figures, `data-backfill-state`, the filter
    caveat, "Unknown" ≠ 0, absent when `/backfills` is empty, and cleared for a long-finished batch.
- Full suites green: State (216), Drivers.MsSql (248), Drivers.Postgres (64), Api (434), Core (193),
  Drivers.Abstractions (62). Web `build` + `lint` clean.

## Decisions

- **A `BackfillBatches` table, not columns on `TaskRuns`.** The planned segment count and the
  one-per-reload estimate are batch facts no single segment carries, and a table is the natural home
  for the future Batch Load History view and a future "cancel the whole batch".
- **No SignalR.** Per-segment "rows copied" only moves when a segment completes; a 2s poll while
  running is more than enough, and `RunMonitorService` has nothing incremental to push anyway.
- **Whole-table estimate even for filtered mappings.** Catalog stats can't answer a predicate, and a
  scaled guess is worse than an honest overcount with the caveat attached.

## Out of scope

- Batch Load History screen (the endpoint's `limit` is the seam for it).
- Live mid-run row counting for the in-flight segment.
- An estimate that accounts for a `source.Filter`.
- DuckDB `ITableRowEstimator` (not a table-backed backfill source).
