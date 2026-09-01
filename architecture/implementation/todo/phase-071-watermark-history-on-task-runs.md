# Phase 71 — Watermark history, carried on `TaskRuns` (planned)

**Status**: Planned, not started — mechanism confirmed feasible by reading the actual completion path.
**Plan reference**: none — small enough to skip a separate planning doc; the design was settled in
conversation (piggyback on `TaskRuns` rather than a new table) and confirmed against the code below.

## The ask

Track how a mapping's change watermark evolves over time, and purge that history automatically using the
same retention criteria already applied to run history (phase 60) — explicitly *not* a new table with its
own pruning logic, if it can ride on `TaskRuns` instead.

## Why this is feasible almost for free

**`ChangeWatermarks` today is current-value only** — `(TaskName, SourceTable)` primary key, one row,
overwritten on every update (`Migrations.cs`). No history exists anywhere today; this phase adds it
without touching that table at all — it stays exactly what it is, the fast lookup a reader consults each
pass for "where did I leave off."

**`RunExecutor.cs` already computes the new watermark at exactly the point it finalizes a run.** Line
~715: `newWatermark = read.WatermarkAfterRead;`. Line ~723: `state.SetWatermark(task.Name, watermarkKey,
newWatermark)` (writing the current-value table). Line 278, in the same method's success path:
`state.CompleteRun(item.RunId, RunStatus.Succeeded, rowsRead, rowsWritten, errorSummary: null, timing:
timing)` — the call that finalizes the `TaskRuns` row. `newWatermark` (and `previousWatermark`, already
in scope from earlier in the same pass) are both available at that call site already — this is a matter
of threading two more values through an existing call, not new plumbing.

**Phase 60's pruning already deletes whole `TaskRuns` rows** (`TaskRunStore.cs`: `DELETE FROM TaskRuns
WHERE RunId IN (...)`), by age and per-mapping count. A new nullable column on that same table is purged
by the existing query with **zero changes to `RunPruningService` or its pruning logic** — this is what
makes "purge them the same way as the runs" free rather than a second mechanism to build and keep in
sync.

## The change

### 1. `TaskRuns` gains two nullable columns

```sql
ALTER TABLE TaskRuns ADD COLUMN PreviousWatermark TEXT NULL;
ALTER TABLE TaskRuns ADD COLUMN NewWatermark TEXT NULL;
```

Both null for run kinds that don't produce a watermark (Backfill, Verification) or for a pass that read
nothing new — populated only when `RunExecutor` actually calls `SetWatermark` for that run, mirroring the
existing `if (item.RunKind == RunKind.Primary && newWatermark is not null)` gate. Recording *both* values
(not just the new one) makes each row self-describing — the delta is visible without cross-referencing
the previous row — and costs nothing extra, since `previousWatermark` is already sitting in scope at the
same call site.

### 2. Thread the two values through the existing completion path

`CompleteRun` is already routed through phase 39's single-writer state model (a local call inside the API,
a network call from a `TaskRunner` child process) — both paths need the same two new parameters:

- `IRunnerState.CompleteRun` (interface) and `LocalRunnerState.CompleteRun` (direct call).
- `RemoteRunnerState.CompleteRun` and `CompleteRunRequest` (`DataSync.State/Remote/StateProtocol.cs`) —
  the wire DTO a `TaskRunner` process posts to the API's loopback endpoint.
- `RunnerStateEndpoints.cs`'s `/complete-run` handler, and `JournalRecovery.cs`'s replay path (for a run
  whose completion had to be journaled offline and applied later — same request shape, same fields).
- `TaskRunStore.CompleteRun`, writing the two new columns.
- `TaskRunRecord` (`DataSync.State/Models.cs`) gains the matching fields, so they're queryable/exposed
  through the same DTO run history already uses.

### 3. `RunExecutor.cs`'s success-path call to `CompleteRun` passes the two values

The failure-path calls (lines ~291, ~302, ~310, ~320) pass `null` for both — a failed run didn't produce
a durable watermark change (the whole point of "advance the watermark only after the write succeeds,"
already established elsewhere in this codebase).

## What this phase does not build

- Any change to `ChangeWatermarks` or the current-value lookup path readers use — untouched.
- Any change to `RunPruningService`/pruning logic — the existing mechanism already covers the new columns.
- A dedicated history *view* in the SPA (a "watermark over time" chart or list) — this phase stores and
  exposes the data through the existing run-history API surface; a dedicated view is a natural, separate
  follow-on, same split phase 59/62 already used (store first, view later) if wanted.

## How to verify when built

- A successful Primary-kind run with a real watermark advance records both `PreviousWatermark` and
  `NewWatermark` on its `TaskRuns` row, matching what `ChangeWatermarks`' current value became.
- A Backfill or Verification run's row has both columns null.
- A failed run's row has both columns null, even if a watermark had been computed before the failure.
- Querying a mapping's run history in order shows the watermark's value at each point in time — a real
  history, not just the current value.
- Pruning a mapping down to its retention cap removes the watermark history for the pruned runs along
  with everything else on those rows — no separate pruning step needed, verified by confirming
  `RunPruningService`'s code is unchanged.
- Full suite green.

## Open questions

- Whether a dedicated SPA view for watermark history is wanted now or later — not asked for explicitly;
  scoped out per "does not build," above, unless redirected.
