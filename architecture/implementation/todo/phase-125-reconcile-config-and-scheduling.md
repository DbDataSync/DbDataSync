# Phase 125 — delete reconciliation: `ReconcileConfig`, a scheduled cadence, and an after-change strategy

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/watermark-delete-detection.md`. Phase 2 of that
design — depends on phase 124 (the `KeyReconcile` reader/writer, `DeleteGuard`,
`RunKind.ReconcileDeletes`, `ReconcileService`).

## Why

Phase 124 makes a key-diff delete sweep a thing an operator triggers by hand. A watermark-tracked
mapping needs it *ongoing* — deletes accumulate silently between manual runs. This phase makes a
sweep a configured, scheduled thing, and lets a Primary pass that just found changes ask for one.

## What this phase will build

### `ReconcileConfig` — `src/DbDataSync.Core/Config/ReconcileConfig.cs` (new)

Replication-level with a per-mapping override, mirroring `ChangeProcessingConfig` +
`PipelineResolution`:

```csharp
public sealed class ReconcileConfig
{
    public bool Enabled { get; set; }
    public SchedulingConfig? Every { get; set; }              // reuse the existing type wholesale
    public AfterChangeStrategy AfterChange { get; set; } = new NoAfterChangeStrategy();
    public DeleteGuard DeleteGuard { get; set; } = new RatioDeleteGuard();   // phase 124 type
    public ReaderConfig? Reader { get; set; }                 // default KeyReconcile
    public CacheConfig? Cache { get; set; }                   // default StagingTable
    public WriterConfig? Writer { get; set; }                 // default KeyReconcileDelete
}
```

- Added to `ReplicationTaskConfig` (required, defaulted) and as `TableMappingConfig.ReconcileOverride`
  (optional). `PipelineResolution` grows `ReconcileReader/Cache/Writer` +
  `LevelOfReconcile{Reader,Cache,Writer}`, the same `??`-per-stage shape it already has for Change
  Processing.
- **`Every` reuses `SchedulingConfig`** (`{ Mode: Continuous|Periodic, FrequencySeconds?,
  CronExpression? }`) verbatim — no new grammar, no new evaluator, and the SPA scheduling editor
  component is reusable. The `IdleTimeoutSeconds` fields on that type are worker-lifetime concepts,
  nullable and simply ignored here.
- **Segmenting stays on the mapping** (`DefaultSegmenting`) — a property of the table, per the
  `bulk-load-pipeline-and-the-initial-load-rule` outcome. `ReconcileConfig` carries no segments.
- `ConfigValidation` — when `Enabled`, the resolved reader/writer must be the `KeyReconcile` pair
  (phase 124's pairing check) and the mapping must have a cached, fully-mapped source primary key;
  `Every` (if set) validates through the existing `SchedulingConfig` rules. Warn (not block) when the
  mapping's Change Processing reader already `DetectsDeletes` — a sweep is then redundant.
- YAML: `DeleteGuardYamlConverter` (phase 124) and a new `AfterChangeStrategyYamlConverter`, both
  mirroring `BatchReloadSegmentYamlConverter`.

### `AfterChangeStrategy` — `src/DbDataSync.Core/Config/AfterChangeStrategy.cs` (new)

A `[JsonPolymorphic]` sealed hierarchy, the shape of `DeleteGuard` / `BatchReloadSegment`:

```csharp
[JsonDerivedType(typeof(NoAfterChangeStrategy), "none")]
[JsonDerivedType(typeof(AfterAnyChangeStrategy), "any")]
public abstract record AfterChangeStrategy;

public sealed record NoAfterChangeStrategy : AfterChangeStrategy;
public sealed record AfterAnyChangeStrategy : AfterChangeStrategy;
```

- `AfterChangeEvaluator.ShouldReconcile(AfterChangeStrategy strategy, long rowsReadSinceLastSweep)` →
  `bool`. Pure. `NoAfterChangeStrategy` → `false`. `AfterAnyChangeStrategy` → `rowsReadSinceLastSweep
  > 0`. A future `AfterNChangesStrategy(int Count)` → `>= Count` — **one number covers it, no
  signature change** (plan Q4).
- The input is a scheduler-computed aggregate, not a single pass's outcome — see below.

### Scheduled cadence + after-change trigger — `src/DbDataSync.Api/Services/SchedulerService.cs`

Both live here, sharing one enqueue path (plan Q3). On each tick, alongside the existing Primary
due-check (`TickContinuousAsync` / `TickPeriodicAsync`):

- `taskRunStore.GetLastReconcileEnqueueByMapping(name)` — a sibling of
  `GetLastPrimaryEnqueueByMapping` filtering `RunKind = ReconcileDeletes`.
- For each mapping with `ReconcileConfig.Enabled`:
  - **cadence due**: `Every is not null && SchedulingEvaluator.IsDue(Every, lastReconcileEnqueue,
    now)`.
  - **after-change due**: `AfterChangeEvaluator.ShouldReconcile(AfterChange, rowsReadSinceLastSweep)`,
    where `rowsReadSinceLastSweep` = `SUM(RowsRead)` over `RunKind = Primary`, `Status = Succeeded`
    runs for this mapping with `EndedAtUtc > lastReconcileEnqueue` (a new `TaskRunStore` query — one
    correlated statement, or a per-candidate-mapping `SumRowsRead(task, mapping, sinceUtc)`).
    Floored by the cadence: after-change never fires more often than `Every` (or, when `Every` is
    unset, a small fixed floor so a busy continuous mapping doesn't sweep every 5s).
  - **dedup**: skip if a `ReconcileDeletes` run for this mapping is already
    `Pending`/`Claimed`/`Running` — `WorkQueueStore`'s `UX_WorkQueue_InFlight` dedups per
    `(task, kind, mapping, segment)`, but a multi-segment sweep needs a mapping-level check:
    `workQueueStore.HasOutstandingWork(name, RunLane.Backfill, ...)` scoped to kind+mapping, or a
    dedicated `HasPendingReconcile(name, mapping)`.
  - due and not deduped → enqueue via a shared `ReconcileService.EnqueueScheduledAsync(replication,
    mapping)` that reuses phase 124's segment expansion + guard resolution + per-segment
    `workQueueStore.Enqueue`.

No new state table for "last swept" — `TaskRuns` already records every `ReconcileDeletes` enqueue and
every `Primary` run's `RowsRead` / `EndedAtUtc`.

### `RunExecutor` — nothing

The after-change decision is made by the scheduler on its own tick, not by the worker after a pass,
so `RunExecutor` gains no hook. (Phase 124 already established that `ReconcileDeletes` needs no
`RunExecutor` branch.)

### Optional: a batch rollup — `ReconcileBatches` (plan Q2)

Only if operators want the "sweep: 12/40 segments, 1,204 rows removed" view. A sibling of
`BackfillBatches` — `{ BatchId, TaskName, MappingName, CreatedAtUtc, SegmentCount }` plus a
`RowsRemoved` aggregate from `TaskRuns` and a `SegmentsWithGuardViolation` count — with its own
`ReconcileBatchStore` and a "Delete reconciliation" section on the Monitoring screen. Kept separate
from `BackfillBatches` because its progress vocabulary ("rows removed", not "rows copied"; no
whole-table denominator) and `BackfillState` naming are reload-shaped and would force branching
through every consumer. **Decide during implementation whether v1 needs it or the plain
per-`ReconcileDeletes`-run history suffices.**

### SPA — `src/DbDataSync.Web/`

- A **"Delete reconciliation"** card on the replication detail (and a mapping-level override),
  beside the Change Processing and segmenting editors: `Enabled`, `Every` (reuse the scheduling
  editor), `AfterChange` (a small type picker), `DeleteGuard` (a type picker with a `MaxRatio`
  field for `ratio`). The strategy and guard render the way the segmenting editor's mode picker
  already does.
- `types.ts` / `api/client.ts` / `api/hooks.ts` for `ReconcileConfig`; the new-replication default
  carries a disabled `ReconcileConfig`.
- If the batch rollup ships: the Monitoring section for it.

## How to verify when built

- `dotnet build` clean; `npm run build` / `lint` clean.
- **`AfterChangeEvaluatorTests`** — per arm: `NoAfterChangeStrategy` never; `AfterAnyChangeStrategy`
  on `rowsReadSinceLastSweep > 0` only. Round-trips through JSON and the YAML converter.
- **`SchedulingEvaluator` reuse** — a `ReconcileConfig.Every` in `Continuous` and `Periodic` modes
  drives `IsDue` exactly as a replication schedule does (existing evaluator tests, new fixtures).
- **`SchedulerService` tests (fake clock, in-memory state)**:
  - a cadence-due mapping enqueues one sweep per `DefaultSegmenting` segment on the backfill lane;
  - `AfterAnyChangeStrategy` enqueues once after a `Primary` run recorded `RowsRead > 0` since the
    last sweep, and not again until another change arrives;
  - the cadence floor caps after-change frequency;
  - dedup: no second sweep enqueued while one is `Pending`/`Running` for the mapping;
  - `NoAfterChangeStrategy` + no `Every` → never enqueued even after changes.
- **`ConfigValidation` tests** — `Enabled` with a keyless mapping / a non-`KeyReconcile` resolved
  reader / an unmapped key column is rejected; the redundant-with-a-delete-detecting-reader case
  warns.
- **Integration (`Category=Integration`)** — end to end: a replication with `ReconcileConfig.Enabled`
  + a short `Every`, a `Watermark` mapping; delete source rows; a scheduler tick enqueues a sweep;
  the spawned `TaskRunner` runs it; the target converges. Then set `AfterAnyChangeStrategy`, insert +
  delete at the source, run a Primary pass, tick — a sweep is enqueued and converges the target.
- Full non-integration and `Category=Integration` suites green; existing scheduler tests unaffected.

## What this phase will not build

- New `AfterChangeStrategy` variants beyond `None` / `AfterAny` — the hierarchy is the extension
  point; the next variant lands with its use case.
- New `DeleteGuard` variants beyond phase 124's `None` / `Ratio`.
- Moving segmenting off the mapping.
- A worker-side after-change hook — the scheduler owns the decision.

## Open questions to resolve during implementation

- **`rowsReadSinceLastSweep` query shape** — one correlated statement per task, or a per-candidate
  `SumRowsRead(task, mapping, sinceUtc)`. Leaning the batch query, matching
  `GetLastPrimaryEnqueueByMapping`'s shape.
- **The after-change floor when `Every` is unset** — a fixed minimum interval, or require `Every`
  whenever `AfterChange` is not `None`. Leaning: require `Every` (a `ConfigValidation` rule) so the
  floor is always explicit.
- **Whether the batch rollup (`ReconcileBatches`) is in scope for this phase** or a follow-on —
  decide from whether the plain run history reads clearly enough for a multi-segment sweep.
- **`HasPendingReconcile` vs. reusing `HasOutstandingWork`** for the mapping-level dedup.
