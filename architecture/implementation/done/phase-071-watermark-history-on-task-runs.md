# Phase 71 — Watermark history, carried on `TaskRuns`

**Status**: Complete.
**Plan reference**: none — small enough to skip a separate planning doc; the design was settled in
conversation (piggyback on `TaskRuns` rather than a new table) and confirmed against the code.

## The ask

Track how a mapping's change watermark evolves over time, and purge that history with the same
retention criteria already applied to run history (phase 60) — explicitly not a new table with its own
pruning logic.

## What was built

### `TaskRuns` gains two nullable columns

A new migration adds `PreviousWatermark` and `NewWatermark`, both `NULL`. `ChangeWatermarks` is
untouched — it stays the current-value lookup a reader consults each pass.

### Threaded through the existing completion path

Two optional parameters, defaulted to null, added to every hop of the `CompleteRun` path:
`IRunnerState.CompleteRun` → `LocalRunnerState` / `RemoteRunnerState` → `CompleteRunRequest`
(`StateProtocol.cs`) → `/complete-run` in `RunnerStateEndpoints.cs` → `JournalRecovery.ApplyRunOutcome`
→ `TaskRunStore.CompleteRun` → the columns → `TaskRunRecord`. Defaulted rather than required for the
same reason `FailureKind` and `Timing` are: a journal entry written by an older runner has to replay,
and the completions that never produce a watermark (`ProcessSupervisor`'s cancel/orphan/stop paths, and
`RunExecutor`'s four failure paths) say "no watermark change" by saying nothing.

`RunsController` returns `TaskRunRecord` directly, so both fields are exposed through the run-history
API with no DTO change.

### `RunExecutor` records only what it made durable

`RunMappingAsync` now returns a fourth tuple element, a `WatermarkChange?` — a private record of
`(Previous, New)`. It is assigned **inside** the existing
`if (item.RunKind == RunKind.Primary && newWatermark is not null)` block, in the same place
`SetWatermark` is called, not beside it. That placement is the correctness argument: the history on
`TaskRuns` cannot claim an advance `ChangeWatermarks` did not take, because the same gate decides both.
The success-path `CompleteRun` passes `watermark?.Previous` / `watermark?.New`; every failure path
passes nothing.

**One nullable record rather than two loose strings.** Threading two nullable strings up from the pass
would have made "did this run move the watermark" a four-state question when only one state matters —
and `previousWatermark` is legitimately null on a first pass, so "both null" and "previous null" needed
to stay distinguishable at the call site rather than by convention.

## The pruning claim, verified rather than assumed

**`RunPruningService` and `TaskRunStore.PruneRuns` are unchanged, and that is correct.** The prune is
`DELETE FROM Logs WHERE RunId IN (…)` followed by `DELETE FROM TaskRuns WHERE RunId IN (…)` — whole-row
deletes naming no columns, so a new column on `TaskRuns` is purged by the existing statement with no
second mechanism to build or keep in sync. Asserted, not assumed, by
`WatermarkHistoryStoreTests.PruningARunRemovesItsWatermarkHistoryWithIt_WithNoSeparateStep`, which
prunes to a per-mapping cap and then queries `TaskRuns` directly for the pruned run's watermark value.

## How it was verified

- **`WatermarkHistoryStoreTests`** (new, 7 tests, unit): both columns round-trip; a first pass records
  a null previous and a concrete new; Backfill and Verification runs leave both null; a failed run
  records no advance; run history in order shows the watermark at each point in time; and the pruning
  test above.
- **`RunExecutorIntegrationTests.EachSuccessfulPass_RecordsTheWatermarkItMovedFromAndTo`** (new,
  integration, real SQL Server): two successive passes, where the second's `PreviousWatermark` equals
  the first's `NewWatermark` and its `NewWatermark` equals what `ChangeWatermarks` now holds — the
  chain, and its agreement with the current-value table.
- **`AFailedPass_DoesNotAcknowledgeAndLeavesTheHistoryAlone`** (extended): now also asserts both columns
  are null on the failed run, alongside the existing acknowledge/watermark assertions.
- The migration runs on all three state engines — the cross-engine integration suite passed unchanged.
- Full suite: unit 866 passed / 0 failed; integration 197 passed / 0 failed.

## Bugs found while building

**The new store tests initially got one run where they asked for three.** `WorkQueueStore.Enqueue`
deduplicates on the in-flight partial unique index, so enqueuing the same
`(task, kind, mapping, segment)` while the first item is still Pending returns the first run's `RunId`
rather than making a second row. Correct for the queue, silent for a test. Fixed the way
`RunPruningTests` already had — a distinct segment label per run — and the history test also backdates
`StartedAtUtc`, since `ORDER BY StartedAtUtc DESC` says nothing useful about three rows written in the
same instant.

## What this phase did not build

- Any change to `ChangeWatermarks` or the current-value lookup readers use — untouched.
- Any change to `RunPruningService` or pruning logic — see above; the whole point was that none was
  needed.
- A watermark-history view in the SPA. The data is stored and exposed through the run-history API; a
  dedicated view is a separate follow-on, the same store-first/view-later split phases 59 and 62 used.
