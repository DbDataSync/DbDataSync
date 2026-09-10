# Phase 124 — key-diff delete detection: the `KeyReconcile` reader/writer pair and an on-demand sweep

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/watermark-delete-detection.md`. Phase 1 of that
design; phase 125 (automation) builds on it. Independent of the 034/035/038 queue and the
phase-109g–109i series. Depends on nothing new — the generic driver layer, `BackfillService`,
`RunKind` / `RunLanes` (phase 108), the phase-104 run-history filters.

## Why

A mapping on the **`Watermark`** reader — or any reader with `IChangeReader.DetectsDeletes => false`
(a scripted change query, a descriptor driver) — sees inserts and updates through its continuous
`Primary` pass but **never sees deletes**: a source row that is removed is simply never selected
again. The only fix today is a full `BatchReload` through `DeleteInsertWriter`, which for every
segment `DELETE`s the whole scope and re-`INSERT`s it from staging — moving every column of every row
to reconcile a handful of absences.

This phase builds the cheap version: read **only the source primary-key values**, delete the target
rows whose key is absent from that set, within the same segment scope, touching nothing else. It runs
on the existing segmented-per-work-item model (`DefaultSegmenting`, `AutoSegment` expansion, one
`WorkQueue` item per segment, the backfill lane).

## What this phase will build

### `DeleteGuard` — `src/DbDataSync.Core/Config/DeleteGuard.cs` (new)

A `[JsonPolymorphic]` sealed hierarchy, the exact shape `BatchReloadSegment`
(`src/DbDataSync.Core/Config/BatchReloadSegment.cs`) already establishes — engine-neutral, in
`Core.Config` so the driver layer can evaluate it:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(NoneDeleteGuard), "none")]
[JsonDerivedType(typeof(RatioDeleteGuard), "ratio")]
public abstract record DeleteGuard;

public sealed record NoneDeleteGuard : DeleteGuard;
public sealed record RatioDeleteGuard(double MaxRatio = 0.5) : DeleteGuard;
```

- `DeleteGuardEvaluator.Check(DeleteGuard guard, long scopeCount, long deleted)` →
  `DeleteGuardResult(bool Ok, string? Message)`. Pure, assertable without a server.
  - `NoneDeleteGuard` → always `Ok`.
  - `RatioDeleteGuard` → not `Ok` when `scopeCount > 0 && (double)deleted / scopeCount > MaxRatio`;
    the message names the observed ratio, the limit, and that a run can override with a "none" guard.
- A YAML converter (`DeleteGuardYamlConverter`, mirroring `BatchReloadSegmentYamlConverter`) for the
  config file in phase 125; `System.Text.Json` polymorphism covers the work-item option here.

### `KeyReconcileReader` — `src/DbDataSync.Drivers.Generic/KeyReconcileReader.cs` (new)

Modelled on `BatchReloadReader` (`src/DbDataSync.Drivers.Generic/BatchReloadReader.cs`); constructor
`(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)`. Implements
`IChangeReader, ISegmentExpandingReader, IStatementPreview`.

- `Kind => GenericDriverKinds.KeyReconcile` (new constant `"KeyReconcile"`).
- `DetectsDeletes => false` — it produces the authoritative in-scope key set; the writer reconciles.
- **Key projection.** From `sourceColumns.RequireAll(mappingName, "source").Where(c => c.IsPrimaryKey)`
  (cache-only per phase 91; `MetadataNotCachedException` if the cache is stale). Filter
  `columnMappings` to those whose `SourceColumn` is a key column and render via
  `SourceProjection.Render(dialect, keyColumnMappings)` — a transform on a key column still applies.
  - **Every source key column must be mapped** — a key absent from `columnMappings` cannot be
    anti-joined on the target. Throw a clear error (also a save-time check, below).
- **Requires a primary key** — a keyless source is a run-time failure here and a save-time rejection
  (below), unlike `DeleteInsertWriter` which needs none.
- **Segment-aware, exactly as `BatchReloadReader`.** `SegmentSerializer.ReadOptional(options)`;
  `SegmentScope.Build(dialect, binder, segment, columns)` (columns resolved only for list/range
  segments). `ExpandAutoSegmentsAsync` is identical to `BatchReloadReader`'s — factor the shared body
  into `SegmentExpansion` (`ExpandAuto(dialect, catalog, source, segments, ct)`) and have both
  readers call it, rather than a third copy.
- **No `BoundedRead`** — `Parameters` does not include `BoundedRead.Descriptor`. The segment is the
  only bound; a capped key scan feeding a delete-absent writer would wipe every key past the cap.
- Emits one `ChangeRow(ChangeOperation.Insert, keySchema, keyValues)` per source key in scope;
  echoes `previousWatermark ?? ""` back as `NewWatermark`, like `BatchReloadReader`.
- `DescribeAsync` returns the keys-only `SELECT`.
- New `KeyReconcileStatement` static class (statement text, assertable without a server), like
  `BatchReloadStatement`.

### `KeyReconcileDeleteWriter` — `src/DbDataSync.Drivers.Generic/KeyReconcileDeleteWriter.cs` (new)

Modelled on `DeleteInsertWriter` (`src/DbDataSync.Drivers.Generic/DeleteInsertWriter.cs`); constructor
`(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)`. Implements
`IChangeWriter, IStatementPreview`. `SupportsReconciliation => true`. Never inserts or updates.

- `Kind => GenericDriverKinds.KeyReconcileDelete` (new constant `"KeyReconcileDelete"`).
- `ApplyAsync`:
  - `var shape = TargetShape.FromCachedColumns(...)` — gives `shape.QuotedTarget` and
    `shape.PrimaryKeyColumns` (already `columns.Where(c => c.IsPrimaryKey)`, target-named).
  - `var scope = SegmentScope.Build(dialect, binder, SegmentSerializer.ReadOptional(options),
    shape.Columns, columnMappings)` — target side, translating a renamed key column.
  - **Staging columns are target-named** (`BatchInsertStagingProvider` stages `mappedTargetColumns`),
    so the anti-join is `staging.<targetKey> = <target>.<targetKey>` directly.
  - One transaction (`DeleteInsertWriter`'s atomicity contract — a concurrent target reader never
    sees a half-reconciled scope):
    1. `SELECT COUNT(*) FROM {shape.QuotedTarget} WHERE {scope.Predicate}` → `scopeCount`
       (`scope.AddTo(cmd)`).
    2. `DELETE FROM {shape.QuotedTarget} WHERE {scope.Predicate} AND NOT EXISTS (SELECT 1 FROM
       {staged.StagingLocation} s WHERE s.{k1} = {shape.QuotedTarget}.{k1} AND …)` →
       `deleted` (`ExecuteNonQueryAsync` rows affected). `NOT EXISTS` correlated — **not** tuple
       `NOT IN`.
    3. `var result = DeleteGuardEvaluator.Check(guard, scopeCount, deleted)`. `if (!result.Ok) throw
       new InvalidOperationException(result.Message)` — the `catch` rolls the transaction back.
    4. `await transaction.CommitAsync(...)`.
  - `guard` from `DeleteGuardOption.Read(options)` — a `deleteGuard` option holding serialized
    `DeleteGuard` JSON; default `RatioDeleteGuard(0.5)` when absent.
  - `return new WriteResult(deleted)`.
- `DescribeAsync` returns the count + delete statements, noting the guard.
- New `KeyReconcileDeleteStatement` static class.

### Kind constants + driver registration

- `src/DbDataSync.Drivers.Generic/GenericDriverKinds.cs` — `KeyReconcile`, `KeyReconcileDelete`.
- `GenericDriver.BuildReaders` / `BuildWriters` switches — new arms for the two Kinds, so a
  descriptor driver opts in through its `capabilities.readers` / `writers` list.
- `MsSqlDriver.cs` (`Readers` / `Writers` lists) and `PostgresDriver.cs` — add
  `new KeyReconcileReader(<Dialect>.Instance, <Catalog>.Instance, <ValueBinding>.Instance)` and the
  writer, **under the generic Kind names** (both drivers already register `WatermarkReader` /
  `BatchReloadReader` / `DeleteInsertWriter` directly this way — no engine-prefixed variant needed).
- `MsSqlDriverKinds.cs` — re-export the two constants (as it does `Watermark`).
- The `StagingTable` / `MsSqlStagingTable` provider is used unchanged.

### `RunKind.ReconcileDeletes` — `src/DbDataSync.State/Models.cs`

- Add to `enum RunKind`. Persisted as `.ToString()` in `TaskRuns` / `WorkQueue` — **additive, no
  data migration** (no existing row carries it).
- `RunLanes.KindsFor` / `LaneFor` → **`RunLane.Backfill`** (like `Verification` — non-incremental,
  can run long). Two one-line edits.
- `RunExecutor` (`src/DbDataSync.TaskRunner/RunExecutor.cs`): **no new branch needed** — every
  `RunKind`-specific line is `== RunKind.Primary` (watermark/intent resolution at ~617, the
  `IReadIntentDeclaring` refusal at ~637, the watermark write at ~901, `MarkProductive` at ~383), so
  `ReconcileDeletes` falls through the same `!= Primary` path `Backfill` does. **Verification item:
  grep for `RunKind.Backfill` used *specifically* (not `!= Primary`) — `MarkProductive`'s check and
  anywhere else — and widen to include `ReconcileDeletes` where the intent is "any non-Primary
  reload-shaped work".**

### On-demand trigger — `ReconcileService` + endpoint

- `src/DbDataSync.Api/Services/ReconcileService.cs` (new), near-identical to `BackfillService`
  (`src/DbDataSync.Api/Services/BackfillService.cs`):
  - validate 1:1 mapping and a cached source primary key;
  - expand `AutoSegment` / `CustomSegment` against the real source (via
    `KeyReconcileReader is ISegmentExpandingReader` and `CustomSegmentExpansion`), exactly as
    `BackfillService.ExpandAsync` does;
  - resolve the run's `DeleteGuard` — the mapping's configured one (phase 125) or, until then,
    `RatioDeleteGuard(0.5)`; `NoneDeleteGuard` when `request.overrideGuard` is set;
  - `workQueueStore.Enqueue(replication, RunKind.ReconcileDeletes, mapping, segment.Describe(),
    SegmentSerializer.Serialize(segment), new WorkItemKinds("KeyReconcile", "StagingTable",
    "KeyReconcileDelete"), batchId)` — one per segment. The `DeleteGuard` rides the writer options
    via a `WithGuard(...)` helper mirroring `RunExecutor.WithSegment`.
  - `supervisor.EnsureWorkerRunning(replication)`.
- `POST /api/replications/{r}/mappings/{m}/reconcile-deletes` — body `{ segments:
  BatchReloadSegment[], overrideGuard?: bool }`. On `TableMappingsController` or `RunsController`,
  beside the backfill trigger. `runLocks.IsLocked(replication, RunKind.ReconcileDeletes, mapping)`
  gate.
- **No batch rollup this phase** (plan Q2): the per-segment runs appear in run history like any
  other `ReconcileDeletes` run. A `ReconcileBatches` view is phase 125's, if wanted.

### Validation — `src/DbDataSync.Core/Config/ConfigValidation.cs`

- A `KeyReconcile` reader (as a `ReaderOverride`, or via phase 125's `ReconcileConfig`) **requires** a
  cached source primary key and every key column mapped → reject otherwise, naming the mapping.
- A `KeyReconcile` reader must pair with a `KeyReconcileDelete` writer and vice versa — reject the
  mismatched combination (`KeyReconcile` + `DeleteInsert` would re-insert NULL-filled key rows).

### SPA — `src/DbDataSync.Web/`

- `RunKindBadge` gains a `ReconcileDeletes` case; the phase-104 run-history `kind` filter gains it
  (`types.ts`, the filter control, `RunsPanel`).
- A "Reconcile deletes" action on the mapping's Backfill area — a segment picker seeded from
  `DefaultSegmenting` (reuse the backfill form's segment editor), an "allow large deletion" checkbox
  → `overrideGuard`, calling the new endpoint. `api/client.ts` + `api/hooks.ts`.
- Capability plumbing so `KeyReconcile` / `KeyReconcileDelete` are **not** offered in the ordinary
  Change Processing reader/writer pickers — they are reconcile-pipeline-only. (A `reconcileOnly`
  flag on the capability DTO, or an explicit exclusion list in the picker components.)

## How to verify when built

- `dotnet build DbDataSync.slnx` clean; `npm run build` / `lint` clean.
- **`tests/DbDataSync.Drivers.Generic.Tests/KeyReconcileStatementTests.cs`** (new, no server): the
  projection is keys-only (single and composite key); `DELETE … NOT EXISTS` renders with the segment
  predicate composed in; backtick / bracket / double-quote quoting; the count-then-delete shape.
- **`DeleteGuardEvaluatorTests`**: `RatioDeleteGuard` over threshold → not `Ok`, message names the
  ratio; under → `Ok`; `scopeCount == 0` → `Ok`. `NoneDeleteGuard` → always `Ok`. Round-trips through
  `System.Text.Json` and the YAML converter.
- **Writer unit/integration**: a guard violation rolls the transaction back and the segment fails
  (target unchanged).
- **Integration (`Category=Integration`, real SQL Server + Postgres containers)**: a `Watermark`
  mapping, source and target seeded equal; at the source delete some rows and update others; run a
  full-segment `ReconcileDeletes` sweep through the real API + a spawned `TaskRunner`; assert the
  deleted rows are gone from the target, the updated rows keep their **old** target value (no
  insert/update happened), untouched rows unchanged.
- **Segmented**: `AutoSegment` over the key column expands to buckets; each bucket's sweep deletes
  only within its own range; a bucket whose source rows were all deleted empties its scope (with
  `overrideGuard`) or fails the ratio guard (without).
- **Validation**: keyless mapping rejects `KeyReconcile`; unmapped key column rejected;
  `KeyReconcile` + `DeleteInsert` rejected.
- **`RunKind` regression**: the existing golden-path / monitoring Playwright suites stay green; a
  `ReconcileDeletes` run shows its badge and is filterable in run history.
- Full non-integration and `Category=Integration` suites green.

## What this phase will not build

- `ReconcileConfig`, the scheduled cadence, the after-change strategy — phase 125.
- A batch rollup / progress card for a sweep — phase 125 if wanted.
- DuckDb support, or a query source with no key concept.
- Update / content-drift detection — this only removes. Row-hash "snapshot diff" is a separate
  question.
- Keyless-mapping support — that stays the `BatchReload` + `DeleteInsert` path.

## Open questions to resolve during implementation

- **Where `DeleteGuardEvaluator` lives** — `DbDataSync.Core.Config` (drivers reference Core) vs. a
  driver-layer home. Leaning Core.Config, beside the type.
- **`ExpandAutoSegmentsAsync` sharing** — extract `BatchReloadReader`'s body into `SegmentExpansion`
  and have both readers call it, or accept a third near-copy (`MsSqlBatchReloadReader` is already
  one). Leaning extract.
- **Preview-picker exclusion mechanism** for the reconcile-only Kinds — a DTO flag vs. a component
  exclusion list.
