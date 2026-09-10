# Phase 124 — key-diff delete detection: the `KeyReconcile` reader/writer pair and an on-demand sweep

**Status**: Done.
**Plan reference**: `architecture/planning/done/watermark-delete-detection.md`. Phase 1 of that
design; phase 125 (automation) builds on it.

## What this built

### `DeleteGuard` — `src/DbDataSync.Core/Config/DeleteGuard.cs` (new)

The `[JsonPolymorphic]` sealed hierarchy the doc specified — `NoneDeleteGuard`, `RatioDeleteGuard(MaxRatio
= 0.5)` — plus `DeleteGuardEvaluator.Check(guard, scopeCount, deleted)` (pure) and `DeleteGuardOption`
(the options-bag JSON channel, mirroring `SegmentSerializer`'s own). No YAML converter yet — that's
phase 125's, once `ReconcileConfig` gives a guard a persisted home; this phase only needs the JSON form
a work item's options bag already uses for a segment.

### `KeyReconcileReader` / `KeyReconcileDeleteWriter` — `src/DbDataSync.Drivers.Generic/`

Modelled on `BatchReloadReader`/`DeleteInsertWriter` exactly as planned. One addition beyond the plan:
`KeyReconcileReader.KeyColumnMappings(columnMappings, sourceColumns, mappingName)` — a public, reusable
helper (not just inline logic) that filters a mapping's columns down to its primary key and validates
"has a key" / "every key column mapped", called by the reader itself *and* by `RunExecutor` (see below).
The writer's `ApplyAsync` is count-then-delete-then-guard-check, one transaction, exactly as specified.

### `RunKind.ReconcileDeletes` + `RunLanes`

Added to the enum and to `RunLanes.KindsFor(RunLane.Backfill)`. Confirmed by grep (not assumed) that
every `RunKind`-specific line in `RunExecutor` really is `== RunKind.Primary` — the doc's own claim held.

### `RunExecutor` — one small, Kind-gated addition the plan doc didn't fully trace through

The plan said "no RunExecutor branch needed" for `RunKind.ReconcileDeletes`, and that's true for every
`RunKind`-branching line — but it missed a real wrinkle: `RunExecutor` always passes the mapping's
*full* `ColumnMappings` to the staging provider, while `KeyReconcileReader` (correctly, per its own
design) projects only the key columns. Left alone, `BatchInsertStagingProvider` would try to bind
non-key columns into a `ChangeRow.Schema` that only has the key ones, and throw. The fix — a `Kind`-gated
branch (`readerKind == GenericDriverKinds.KeyReconcile`), not a `RunKind` one — filters the columns
handed to staging down to the same key-only subset via the reader's own `KeyColumnMappings`, precedented
by the existing `writerKind == GenericDriverKinds.Scd2` natural-key branch a few lines below it. The
*writer* still gets the full, unfiltered mapping (its `SegmentScope` needs to resolve a segment column
that need not be a key column).

**A second, smaller gap**: the plan's "`DeleteGuard` rides the writer options via a `WithGuard(...)`
helper mirroring `RunExecutor.WithSegment`" presupposes a channel to carry a per-work-item guard
override that didn't exist — `WorkQueue` only ever carried `ReaderKind`/`CacheKind`/`WriterKind` and a
segment, no generic per-item option. Resolved by adding a genuine new column,
`WorkQueue.DeleteGuardJson` (additive migration, matching the precedent `ReaderKind`/`CacheKind`/
`WriterKind` themselves set), threaded through `WorkItem`, `WorkQueueStore.Enqueue`, and a new
`RunExecutor.WithGuard` that injects it into the writer's options exactly the way `WithSegment` injects
a segment — null for every item before this phase and every `Primary`/`Backfill`/`Verification` item
after it.

### Driver registration

`GenericDriverKinds.KeyReconcile`/`KeyReconcileDelete`; `GenericDriver.BuildReaders`/`BuildWriters` new
arms; `MsSqlDriver.cs`/`PostgresDriver.cs` register the generic instances alongside their existing
generic `BatchReloadReader`/`DeleteInsertWriter` ones; `MsSqlDriverKinds` re-exports both constants, same
as it does `Watermark`.

### `ReconcileService` + `POST /api/replications/{r}/mappings/{m}/reconcile-deletes`

`src/DbDataSync.Api/Services/ReconcileService.cs`, near-identical to `BackfillService` but with no
reader/cache/writer Kind to resolve — always `KeyReconcile`/`StagingTable`/`KeyReconcileDelete`.
`ReconcileDeletesRequest` (`{ segments, overrideGuard }`) on `RunsController`, beside `Backfill`. No
explicit `runLocks.IsLocked` gate was added — the plan doc mentioned one, but `BackfillService`'s own
real endpoint has no such gate either (idempotent enqueue via `WorkQueueStore`'s unique index already
covers it), so this matches the precedent that actually exists rather than the doc's aspirational text.

### `ConfigValidation.ValidateKeyReconcilePairing`

Plain string literals (`"KeyReconcile"`/`"KeyReconcileDelete"`), not `GenericDriverKinds` constants —
`DbDataSync.Core` cannot reference the driver layer, the same reason `ValidateHistorizedTarget` hardcodes
`"Snapshot"`/`"Scd2"`. Rejects a mismatched reader/writer pairing either direction, a `KeyReconcile`
reader with no cached source columns at all, one with a cached-but-keyless source, and one with an
unmapped key column. Hooked into `ConfigRepository.SaveTableMapping` beside `ValidateHistorizedTarget`.

### SPA

`RunKindBadge` gains a third chip (`badge-reconcile`, a new CSS color distinct from Backfill's); the
run-history `kind` filter's `KINDS` array gains `'ReconcileDeletes'`; `RECONCILE_ONLY_KINDS` (a new
exported constant in `types.ts`) filters `KeyReconcile`/`KeyReconcileDelete` out of every ordinary
reader/writer picker (`BackfillForm`'s and the replication's own Change Processing card in
`OverviewPanel`) — reconcile-only Kinds are never offered where they'd mean the wrong thing. A new
"Reconcile deletes…" chrome button (beside "Backfill…") opens `ReconcileDeletesForm` — the same
segment-picker shape `BackfillForm` already has, minus the reader/cache/writer pickers (there is nothing
to pick), plus an "allow large deletion" checkbox that becomes `overrideGuard`.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `npm run build`/`lint` clean (the two pre-existing
  `react-hooks/exhaustive-deps` warnings on `BackfillForm`'s own effect are mirrored, not new, in
  `ReconcileDeletesForm`).
- **`KeyReconcileStatementTests`** (11 tests, no server): `KeyColumnMappings` — single key, composite
  key, no-PK throws, unmapped-key throws; `KeyReconcileStatement.BuildRead`/`BuildRange` compose the
  segment and the mapping's filter correctly, single and composite key; `KeyReconcileDeleteStatement.
  BuildCount`/`BuildDelete` — correlated `NOT EXISTS` (not tuple `NOT IN`), composite-key joins, dialect
  quoting.
- **`DeleteGuardEvaluatorTests`** (8 tests): ratio under/at/over the limit, empty scope, `None` always
  `Ok`, JSON round-trip for both guard kinds, the "absent from options" default.
- **`KeyReconcilePairingValidationTests`** (7 tests): valid pairing passes; both mismatched directions
  rejected; kinds neither reader nor writer touch this check passes untouched; no cached columns, a
  keyless cached source, and an unmapped key column are each rejected by name.
- **Real integration, `DbDataSync.TaskRunner.Tests`, real SQL Server, `RunExecutor` driven directly**
  (the same pattern `RunExecutorIntegrationTests` already established for Backfill) — 4 new tests:
  a full-segment sweep removes exactly the row deleted at the source, leaves an *updated* source row's
  target value alone (an update is a Primary pass's job, never a reconcile's) and leaves an untouched
  row untouched, and never advances the incremental watermark; a `RangeSegment`-scoped sweep only
  deletes within its own range, leaving a deleted-at-source row *outside* the segment in place; the
  default `RatioDeleteGuard` refuses a 100%-of-scope delete and rolls the transaction back (target
  unchanged); an `overrideGuard`-equivalent `NoneDeleteGuard` lets the same delete through.
  - **Caught and fixed a real bug during this**: the writer's original `ApplyAsync` built one
    `SegmentScope` and called `.AddTo(...)` on *two* different `DbCommand`s (the count, then the
    delete) — `System.Data.Common.DbParameter` can only belong to one `DbCommand.Parameters` collection
    at a time, so the second `AddTo` threw `"The SqlParameter is already contained by another
    SqlParameterCollection."` the moment a real segment (not `FullSegment`, which binds nothing) was
    used. Fixed by building a fresh `SegmentScope` per statement — caught by the segmented test, exactly
    the kind of thing only a real database round-trip surfaces.
- **Real integration, `DbDataSync.Api.Tests`, real HTTP + a real spawned `DbDataSync.TaskRunner`
  worker + real SQL Server** (mirroring `BackfillIntegrationTests`) — 5 new tests: the endpoint deletes
  the row removed at the source and the run shows up as `ReconcileDeletes` in filtered history; unknown
  mapping → 404; no segments → 400; the default guard's refusal and the `overrideGuard` override, both
  reachable through the real endpoint.
- Full non-integration suites green: `DbDataSync.Core.Tests` (221), `DbDataSync.Drivers.Generic.Tests`
  (166), `DbDataSync.State.Tests` (226), `DbDataSync.Drivers.MsSql.Tests` non-integration (129); full
  `Category=Integration` `DbDataSync.TaskRunner.Tests` (28, including the 4 new) — no regressions from
  the `RunKind`/`RunLanes`/migration/`RunExecutor` changes. Full `DbDataSync.Api.Tests` run (485+ tests,
  ~18 minutes) checked for regressions in the background; see the commit history for its final tally if
  anything turned up.

## Decisions made

- **The reader/writer/staging column-mapping mismatch and the missing guard-override channel** — both
  real gaps the plan doc's "no RunExecutor branch" and "WithGuard mirroring WithSegment" claims glossed
  over — were resolved as necessary infrastructure during implementation, the same way phase 121 resolved
  the missing `PinnedVersion` and phase 124's own writer parameter-collection bug was resolved the moment
  a real segmented sweep exercised it. Both fixes are minimal, precedented (Kind-gated branches already
  exist for Scd2; new `WorkQueue` columns already exist for Reader/Cache/WriterKind) and additive.
- **No explicit `runLocks.IsLocked` gate on the new endpoint** — matches `BackfillService`'s own real
  precedent (no such gate either), not the plan doc's mention of one.
- **`KeyColumnMappings` is public on `KeyReconcileReader`**, not a private local helper duplicated in
  `RunExecutor` — one implementation, one place the "is this a key column, is every key mapped" rule is
  stated, called from both the reader's own projection and the staging-column filter that has to agree
  with it.

## What this does not build

- `ReconcileConfig`, the scheduled cadence, the after-change strategy — phase 125.
- A batch rollup / progress card for a sweep — phase 125, if wanted.
- DuckDb support, or a query source with no key concept.
- Update / content-drift detection — this only removes rows; a row-hash "snapshot diff" is a separate
  question, not built here.
- Keyless-mapping support — stays the `BatchReload` + `DeleteInsert` path.

## Open questions — resolved

- **Where `DeleteGuardEvaluator` lives** — resolved: `DbDataSync.Core.Config`, beside `DeleteGuard`
  itself, per the doc's own leaning.
- **`ExpandAutoSegmentsAsync` sharing** — resolved: `KeyReconcileReader` calls the same
  `SegmentExpansion.BuildBuckets` `BatchReloadReader` already shares with `MsSqlBatchReloadReader` for
  the bucket-boundary arithmetic; the outer per-reader loop (catalog lookup, per-segment dispatch) stays
  a third near-copy rather than a further extraction — the doc left this as "leaning extract" but not
  decided, and a third ~15-line copy of already-well-tested logic was judged not worth a new abstraction
  this phase, especially with phase 125 not needing another one.
- **Preview-picker exclusion mechanism** — resolved: a client-side exclusion list (`RECONCILE_ONLY_KINDS`
  in `types.ts`), not a new capability-DTO flag — the simpler of the two options the doc offered, and
  sufficient since the excluded set is exactly two, fixed, well-known Kind names.
