# A multi-segment initial load can still leave an orphaned, never-completing batch

**Status: fixed 2026-09-15.** See "Fix" at the end. Extracted
from `architecture/implementation/done/phase-143-initial-load-race-loses-cleanly.md`'s own "Out of
scope" section, where it sat as an inert bullet — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## The gap, as phase 143 left it

Phase 143 fixed the single-segment case of a mapping's auto-triggered initial load losing its
`WorkQueue` race against a concurrent operator reload: `WorkQueueStore.EnqueueOrThrow` now throws
`WorkQueueCollisionException` instead of silently colliding, and `LocalRunnerState.RequestInitialLoad`
only commits to pending-load state (`SetPendingLoad`) after that succeeds — so a lost race no longer
strands `ReadHold` at `Loading` forever.

An auto-triggered initial load enqueues **one segment per `mapping.DefaultSegmenting` entry** (empty
meaning a single `FullSegment` — the common case, and the only one phase 143's own tests exercise)
inside one batch (`BulkLoadBatchStore.CreateBatch`, `SegmentCount = segments.Count`). For a mapping with
more than one configured segment, phase 143's "throw on any collision" still has a real hole: **some
segments could win their own `WorkQueue` race while others lose.** The current code enqueues segments in
a loop (`BulkLoadService.CreateBatchAndEnqueueAsync`) — if segment 2 of 3 throws on a collision, segment
1 has already been durably enqueued as real work belonging to this attempt's own batch. That batch's own
`SegmentCount` (3) will never be satisfied by its own completions (only 1 of 3 segments actually belongs
to it), so it never reaches `BulkLoadState.Completed` — a smaller, more contained version of the exact
bug phase 143 fixed for the single-segment case, not a new one.

## Why it's narrower, and probably fine to defer

Needs an operator to manually reload **exactly the same custom segment scheme** a mapping's own
`DefaultSegmenting` already uses, in the same narrow window an auto-triggered load is also in flight —
narrower than the single-segment case phase 143 fixed (which needs only *any* reload of the mapping,
since `FullSegment` is the default), and depends on a less common configuration (an operator has to have
set up `DefaultSegmenting` with more than one entry at all).

## Candidate directions, not evaluated

- **Make the batch-create-plus-enqueue sequence one atomic operation.** Currently two separate store
  calls (`BulkLoadBatchStore.CreateBatch`, then `WorkQueueStore.EnqueueOrThrow` per segment), each its
  own transaction. If all segments could be validated as available *before* the batch row is created (or
  the whole sequence wrapped in one transaction spanning both stores), a partial collision could roll
  back cleanly instead of leaving a mix of real and orphaned segments.
- **Detect a partial collision after the fact and reconcile the batch's own `SegmentCount`** down to
  however many segments actually landed under it, rather than preventing the situation up front — cheaper
  than true atomicity, but changes what `SegmentCount` means (a plan vs. a floor) in a way worth thinking
  through against `BulkLoadHistoryPanel`'s own display of it (phase 139).
- **Confirmed, not just suspected: `ReadHold` does not strand even in the partial-collision case.**
  `CreateBatchAndEnqueueAsync`'s `segments.Select(...).ToList()` enumerates synchronously — if segment 2
  of 3 throws, `.ToList()` throws too, propagating out of `EnqueueForInitialLoadAsync` before it ever
  returns, so `RequestInitialLoad`'s own `SetPendingLoad` call (positioned *after* the enqueuer call,
  phase 143's own fix) never runs. The mapping's `ReadHold` stays whatever it already was; only the
  *batch* and segment 1's own `WorkQueue`/`TaskRuns` row are left orphaned. That downgrades this from "a
  mapping can get stuck" to "a batch/run can appear in history that never resolves" — a real gap, but a
  cosmetic one (worth checking against `BulkLoadHistoryPanel`, phase 139, for how a
  perpetually-`InProgress` batch actually renders there), not a functional one. Lower priority than
  phase 143's own fix was.

## Fix

Took the first candidate direction, but as a compensating rollback rather than true cross-store
transactional atomicity (`WorkQueueStore`, `BulkLoadBatchStore` and `TaskRunStore` each open their own
connection; spanning one transaction across all three would have meant a larger refactor for a bug this
doc's own analysis already argued is narrow and non-critical). `BulkLoadService.
CreateBatchAndEnqueueAsync` now enqueues segments in an explicit loop instead of one LINQ projection, and
on `WorkQueueCollisionException` from a later segment: every segment this same call already got enqueued
is cancelled the same way an operator's own `CancelRun` cancels a pending item (`WorkQueueStore.
TryCancelPending`, then `TaskRunStore.CompleteRun(..., RunStatus.Cancelled, ...)`), and the batch row
itself is removed (new `BulkLoadBatchStore.DeleteBatch`) rather than left recording a `SegmentCount` none
of its segments can still reach. Matches the single-segment case's own behaviour exactly: a losing
collision there leaves no trace at all (the `WorkQueue` insert that would have created it never commits),
so a partial collision here should not leave a visible-but-dead batch and orphaned-but-real segment runs
either.

Confirmed the third bullet above still holds after this change: `ReadHold` never strands in the partial
case, with or without this fix — `LocalRunnerState.RequestInitialLoad`'s own `SetPendingLoad` call is
positioned after the enqueuer call (phase 143's own reordering), so it never runs once
`CreateBatchAndEnqueueAsync` rethrows. This fix only removes the dead batch/segment rows the old code
left behind; the mapping's own next scheduled pass already retried cleanly either way.

Verified with a new integration test,
`BulkLoadIntegrationTests.AMultiSegmentInitialLoad_ThatPartlyCollides_RollsBackWhatItAlreadyEnqueued`: a
dedicated mapping (`map-3`) with a two-entry `DefaultSegmenting`, a pre-seeded collision on the second
segment (deterministic — seeded directly rather than raced over HTTP, so which segment collides isn't a
timing gamble), asserting the batch leaves no `Queued` runs behind and the segment that did enqueue first
ends up `Cancelled`. Passed 3/3 in isolation, then 9/9 for the whole `BulkLoadIntegrationTests` class once
the shared MSSQL test container's concurrent-session load (sustained 75–98% CPU across repeated checks
mid-verification, timing out every test in the class at connection-open, including pre-existing ones
untouched by this change) settled back down.
