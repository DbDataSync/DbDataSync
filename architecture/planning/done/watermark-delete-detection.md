# Delete detection for watermark change tracking — a key-diff sweep

**Resolved 2026-09-09.** The four open questions were settled (see *Open questions* below, now
answered inline). The design builds as two phases:

| phase | what |
| --- | --- |
| **124** — `architecture/implementation/todo/phase-124-key-reconcile-delete-detection.md` | the `KeyReconcile` reader + `KeyReconcileDelete` writer, `DeleteGuard`, `RunKind.ReconcileDeletes`, an on-demand `ReconcileService` + endpoint |
| **125** — `architecture/implementation/todo/phase-125-reconcile-config-and-scheduling.md` | `ReconcileConfig`, a scheduled cadence reusing `SchedulingConfig`, a pluggable `AfterChangeStrategy` evaluated in the scheduler |

The cheap "snapshot diff" `change-tracking-strategies.md` named and left for "a document of its own".
This is that document; the rest of it is the thinking the phase docs carry forward.

---

## The problem

A mapping on the **`Watermark`** reader — or any reader with `IChangeReader.DetectsDeletes => false`
(a scripted change query, a descriptor driver) — sees inserts and updates through its continuous
`Primary` pass but **never sees deletes**. A row removed at the source is simply never selected
again. `WatermarkReader`'s own doc says so; phase 17 surfaces it in the SPA.

The only remedy today is a full **`BatchReload`** through a reconciling writer (`DeleteInsertWriter`):
for each segment, `DELETE FROM target WHERE <scope>` then re-`INSERT … SELECT … FROM staging`. It
converges the target, but it moves *every column of every row* in the segment to catch the handful
that were deleted. On a wide or large table that is the whole cost of a reload, paid to reconcile a
few absences.

## The idea

Read **only the primary-key values** from the source (a covering-index scan, cheap), and delete the
target rows whose key is **absent** from that set — within the same segment scope. Touch nothing
else: an updated-but-not-deleted row keeps its current target value (the watermark pass will bring
the update), an unchanged row is untouched, and no row is re-inserted.

Run it on the **same segmented-per-work-item model** the backfill path already uses: segments from
`TableMappingConfig.DefaultSegmenting` (Full / List / Range / Auto / Custom), `AutoSegment` expanded
against the real source, one `WorkQueue` item per segment, the backfill lane.

This is a complement to the watermark reader, not a replacement: inserts and updates keep flowing
through Change Processing; this sweep only removes.

---

## Design

### A new reader / writer pair — engine-neutral

**`KeyReconcileReader`** (`Kind = "KeyReconcile"`, `DbDataSync.Drivers.Generic`). Modelled on
`BatchReloadReader`, differing only in projection:

- projects **only the mapping's key columns**, resolved from the cached `SourceColumns`
  (`CachedColumn.IsPrimaryKey`), cache-only per phase 91;
- **requires a primary key** — a keyless mapping cannot use it (unlike `DeleteInsertWriter`, which
  needs none). Rejected at save time and guarded at run time;
- segment-aware exactly as `BatchReloadReader` (`SegmentScope`, `ISegmentExpandingReader` reusing
  `SegmentExpansion.BuildBuckets`);
- `DetectsDeletes => false` — it produces the authoritative in-scope key set; the writer reconciles,
  the same contract shape `BatchReloadReader` has;
- **no bounded read** — the segment is the only bound; a capped key scan followed by a delete-absent
  writer would wipe every key past the cap.

**`KeyReconcileDeleteWriter`** (`Kind = "KeyReconcileDelete"`, `DbDataSync.Drivers.Generic`).
Modelled on `DeleteInsertWriter`; `SupportsReconciliation => true`; never inserts or updates. In one
transaction (the `DeleteInsertWriter` atomicity contract — a concurrent target reader never sees a
half-reconciled scope):

1. `SELECT COUNT(*) FROM <target> WHERE <segmentScope>` → `scopeCount`
2. `DELETE FROM <target> WHERE <segmentScope> AND NOT EXISTS (SELECT 1 FROM <staging> s WHERE
   s.<k1> = <target>.<k1> AND …)` → `deleted`. `NOT EXISTS` correlated — not tuple `NOT IN`, which
   is unportable and NULL-broken.
3. evaluate the **delete guard** (below); on a violation, roll back and fail the segment.

Staging is the existing `StagingTable` provider unchanged — the key set stages into a target-side
temp table exactly as reload rows do, and the writer anti-joins against it. Pipeline:
`KeyReconcileReader → StagingTable → KeyReconcileDeleteWriter`.

The reader and writer are a **matched pair**: a `KeyReconcile` reader with a `DeleteInsert` writer
would re-insert NULL-filled key rows. Save-time validation enforces the pairing, and the pair does
not appear in the ordinary Change Processing reader/writer pickers.

### The delete guard — a sealed hierarchy

`DbDataSync.Core.Config.DeleteGuard`, a `[JsonPolymorphic]` sealed hierarchy in the shape
`BatchReloadSegment` already establishes, engine-neutral so the driver layer can evaluate it:

- **`NoneDeleteGuard`** — no limit; delete whatever the diff yields.
- **`RatioDeleteGuard(double MaxRatio = 0.5)`** — a violation when `scopeCount > 0 && deleted /
  scopeCount > MaxRatio`. `scopeCount == 0` is always fine.
- room for `MaxRowsDeleteGuard`, an absolute-floor guard, a require-confirmation guard — each is a
  new derived record plus a `switch` arm in `DeleteGuardEvaluator.Check(guard, scopeCount, deleted)`
  (pure, assertable without a server).

The guard is why an empty staged key set is safe by default: a misconfigured segment column (wrong
name, renamed source table) that reads zero keys trips `Ratio` and fails loudly; a genuine
full-partition purge runs with `None` or a high ratio, deliberately. A run may override the
configured guard with `None`.

### `RunKind.ReconcileDeletes`

A new `RunKind`, riding the **Backfill lane** (non-incremental, can run long — like `Verification`).
It is persisted as an enum name in `TaskRuns` / `WorkQueue`, so adding the value is **additive — no
data migration**; the phase-104 run-history `kind` filter and the SPA `RunKindBadge` gain a case.
`RunExecutor` handles it like `Backfill` — `null` watermark, never touches the cursor (the existing
`RunKind == Primary` guards already exclude it from watermark persistence and `MarkProductive`).

### Three ways to trigger a sweep

All three enqueue one `ReconcileDeletes` work item per segment, guard resolved, on the backfill lane
— the difference is only who decides.

1. **On-demand** — a `ReconcileService` mirroring `BackfillService`: expand `AutoSegment`s against
   the real source, enqueue per segment, reuse the batch-id / per-segment progress the Monitoring
   screen already renders. `POST /api/replications/{r}/mappings/{m}/reconcile-deletes` with
   `{ segments, overrideGuard }`. Locked by `runLocks.IsLocked(replication, ReconcileDeletes,
   mapping)`, same shape as Backfill.

2. **Scheduled cadence** — `ReconcileConfig.Every`, a duration or cron validated by the existing
   `SchedulingEvaluator`. On a tick, `SchedulerService` enqueues a sweep for each enabled mapping
   whose last `ReconcileDeletes` enqueue is older than the cadence (`MAX(enqueued) WHERE RunKind =
   ReconcileDeletes AND MappingName = …`, a sibling of `GetLastPrimaryEnqueueByMapping` — no new
   state table).

3. **An after-change strategy** — `ReconcileConfig.AfterChange`, itself a sealed hierarchy
   (`NoAfterChangeStrategy` default, `AfterAnyChangeStrategy`, room for `AfterNChangesStrategy`, a
   row-threshold strategy, a delete-hint strategy). After a successful `Primary` pass, `RunExecutor`
   evaluates `AfterChangeEvaluator.ShouldReconcile(strategy, passOutcome)`; if true it enqueues a
   sweep — **deduped** (skip if one is already `Pending`/`Running` for the mapping) and floored at
   `ReconcileConfig.Every`.

### `ReconcileConfig` — the second pipeline

Replication-level with a per-mapping override, mirroring `ChangeProcessingConfig` +
`PipelineResolution`: `Enabled`, `Every`, `AfterChange`, `DeleteGuard`, and a `Reader` / `Cache` /
`Writer` defaulting to `KeyReconcile` / `StagingTable` / `KeyReconcileDelete` (overridable for
consistency with the rest of the pipeline model). **Segmenting stays on the mapping**
(`DefaultSegmenting`) — a property of the table, per the `bulk-load-pipeline-and-the-initial-load-rule`
outcome.

### Engine coverage

The key `SELECT` and the `DELETE … NOT EXISTS` are portable, so **SQL Server registers the generic
reader and writer** (as `MsSqlDriverKinds` already re-exports `Watermark`); `MsSqlStagingTable`
(SqlBulkCopy of the small key set) is used unchanged. Postgres and every descriptor driver get the
generic pair the same way `DeleteInsert` is already shared. DuckDb is out of scope for v1.

---

## Phasing

- **Phase 1 — the mechanism.** The reader/writer pair, `DeleteGuard` (`None` + `Ratio`),
  `RunKind.ReconcileDeletes`, the on-demand `ReconcileService` + endpoint + SPA action, save-time
  validation, engine registration. Delivers a manually-triggered, guarded delete sweep.
- **Phase 2 — automation.** `ReconcileConfig` + `PipelineResolution` extension, the scheduled cadence
  in `SchedulerService`, the `AfterChangeStrategy` hierarchy + `AfterChangeEvaluator` +
  the `RunExecutor` hook, the "Delete reconciliation" settings card.

---

## What this does not do

- **Detect updates.** It only removes. Updates are the watermark reader's job and keep flowing
  through Change Processing.
- **Replace a reconciling reload.** A mapping that also needs to repair *content* drift (a target
  row changed behind the replication's back) still wants a `BatchReload` through `DeleteInsert`.
  This is the narrow, cheap tool for the delete case.
- **Work without a primary key.** `DeleteInsertWriter`'s replace-the-scope model needs no key; this
  one's anti-join does. A keyless mapping keeps the reload.
- **Row-hash diffing.** No per-row hash, no update detection — that is a heavier "snapshot diff"
  and a separate question.
- **DuckDb**, or a query source with no key concept.

---

## Open questions — resolved 2026-09-09

1. **Cadence grammar for `ReconcileConfig.Every`.** → **Reuse `SchedulingConfig` wholesale**
   (`{ Mode: Continuous|Periodic, FrequencySeconds?, CronExpression? }`) and the existing
   `SchedulingEvaluator.IsDue`. No new grammar, no new evaluator; the SPA scheduling editor is
   reusable. The `IdleTimeoutSeconds` fields on that type are ignored here.
2. **Batch rollup — reuse `BackfillBatchStore` or a sibling.** → **No batch rollup in phase 124.**
   Per-segment `ReconcileDeletes` runs show in run history like any other run. Phase 125 adds a
   sibling `ReconcileBatches` view *only if* the plain history reads unclearly for a multi-segment
   sweep — never a `Kind` column on `BackfillBatches` (its "rows copied" / whole-table-denominator
   vocabulary is reload-shaped and would force branching through every consumer).
3. **After-change trigger location.** → **In `SchedulerService`**, sharing the enqueue + dedup path
   with the scheduled cadence. Not `RunExecutor`: the enqueuer and `AutoSegment` expansion live in
   the API, the scheduler already ticks doing exactly this kind of due-check, and ~5s latency is a
   non-issue for a delete sweep.
4. **Evaluator signature width.** → **Both minimal.** `DeleteGuardEvaluator.Check(guard, scopeCount,
   deleted)` covers every row-arithmetic guard (a require-confirmation guard uses the run's override
   flag, not row numbers). `AfterChangeEvaluator.ShouldReconcile(strategy, rowsReadSinceLastSweep)`
   takes one scheduler-computed aggregate, which covers both `AfterAnyChange` (`> 0`) and a future
   `AfterNChanges(n)` (`>= n`) with no signature change.

---

## References

- `architecture/planning/done/change-tracking-strategies.md` — §*The strategies* → *Snapshot diff*,
  which this realises (keys-only, as a watermark complement rather than a standalone sync).
- `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md` — the segmenting /
  per-work-item model and the "segmenting stays on the mapping" decision this reuses.
- `src/DbDataSync.Drivers.Generic/BatchReloadReader.cs`, `DeleteInsertWriter.cs`, `SegmentScope.cs` —
  the reader/writer and scoping this is modelled on.
- `src/DbDataSync.Api/Services/BackfillService.cs` — the enqueue-per-segment path `ReconcileService`
  mirrors.
