# Phase 73 — `TaskRuns` timestamps, named for the moment each one records

**Status**: Not started.
**Plan reference**: `architecture/planning/done/task-run-timestamp-naming.md`

## The problem

Phase 72 added `TaskRuns.ClaimedAtUtc`, but wrote it inside `TaskRunStore.BeginRun` — the point a
worker has already claimed an item and is starting to execute it. `WorkQueue.ClaimedAtUtc` (unchanged,
written in `WorkQueueStore.TryClaimNext`) is the genuine claim moment, earlier. Same column name, two
tables, two different moments.

`TaskRuns.StartedAtUtc` has the same problem the other direction, and older: `WorkQueueStore.Enqueue`
writes it at enqueue time, not at execution start. Phase 72 flagged this as known debt and left it
(see that phase's "What this phase did not build"); this phase is exactly that follow-up.

Fix all three names so each records what it says, matching the pattern `WorkQueue` already gets right.

## What to build

### Schema

One migration, `TaskRuns`:

- Add `EnqueuedAtUtc` (`{{addcolumn}}`, nullable at the DDL level to satisfy every dialect, but
  immediately backfilled — unlike phase 72's `ClaimedAtUtc`, this one **can** be backfilled, because
  every existing row's current `StartedAtUtc` value already *is* its enqueue time): a same-migration
  `UPDATE TaskRuns SET EnqueuedAtUtc = StartedAtUtc` right after the `ADD COLUMN`, so no historical row
  loses this figure the way `ClaimedAtUtc` unavoidably did.
- `StartedAtUtc` and `ClaimedAtUtc` keep their existing column names and types — no
  `RENAME COLUMN`/`sp_rename` needed, no new dialect token to build. Only the write sites move (below).

### Write sites

- `WorkQueueStore.Enqueue` — the `INSERT INTO TaskRuns` writes `EnqueuedAtUtc` instead of `StartedAtUtc`
  (`StartedAtUtc` is left null at insert time; a queued-but-unclaimed run has no start yet, correctly).
- `WorkQueueStore.TryClaimNext` — the claim `UPDATE ... WorkQueue SET Status = 'Claimed', ClaimedAtUtc =
  $now, ...` gains a sibling `UPDATE TaskRuns SET ClaimedAtUtc = $now WHERE RunId = $runId`, in the same
  transaction. `TryClaimNext` doesn't currently wrap its update in a transaction (it's two separate
  `database.Retry` calls — select, then conditional update); wrap the update step so a run can't end up
  `Claimed` in `WorkQueue` without a claim time in `TaskRuns`, the same guarantee `BeginRun`'s
  single-statement update gives `Status`/`ClaimedAtUtc` today.
- `TaskRunStore.BeginRun` — stop writing `ClaimedAtUtc`; write `StartedAtUtc = DateTimeOffset.UtcNow`
  instead, in the same `UPDATE` that sets `Status`/`Pid` (same one-statement atomicity phase 72 built,
  just retargeted to the right column).

### Model / API

`TaskRunRecord` (`Models.cs`) gains `EnqueuedAtUtc`, ordered before `StartedAtUtc` (enqueue → claim →
start → end, matching execution order same as phase 72 did for `ClaimedAtUtc`). Update the XML doc on
`StartedAtUtc`/`ClaimedAtUtc` — phase 72's comments describe the old (wrong) semantics and need
rewriting, not just the code. `RunsController` returns the record directly; check whether that still
holds or a DTO now needs the new field explicitly.

### Consumers that need to switch columns, not just names

Phase 72's own audit is the checklist — everywhere it found `StartedAtUtc` used for "when was this asked
for" now wants `EnqueuedAtUtc`, since that's the meaning that column was actually serving:

- `TaskRunStore.PruneRuns`/`DoomedRuns` — retention ranking.
- `RunMetricsStore`'s window selection (which runs count toward "last 24 hours").
- `SchedulingEvaluator` — due-ness against the last run.
- `RunsPanel`'s row timestamp (`clock(r.startedAtUtc)` → `clock(r.enqueuedAtUtc)`).
- `src/DataSync.Web/src/api/types.ts` — add `enqueuedAtUtc`, and audit call sites of the existing
  `startedAtUtc` field the same way, since its meaning is changing under the same name.

Unaffected (confirmed by phase 72, still true): `GetRecentlyEndedRuns`, `ReadLastCompletedPass`,
`RunMonitorService`.

### Durations, renamed and recomputed

- `RunMetricsStore.ReadDurations` (phase 36's p50/p95/max) — computes `EndedAtUtc - StartedAtUtc` now
  (processing time, using the corrected `StartedAtUtc`). Consider renaming the method/field to
  `ReadProcessingTimes` or similar — the point of this phase is that "duration" is exactly the kind of
  name that hides which two timestamps it's between.
- A queue-time/wait-time figure: `StartedAtUtc - EnqueuedAtUtc`. Expose it the way phase 72 exposed the
  old (mistimed) queue wait — raw timestamps, `RunsPanel`'s duration-cell tooltip — just recomputed from
  the corrected pair, and relabeled "queue time"/"wait time" rather than left implicit.
- Both null-guard the same way phase 72 did: no contribution to percentiles, no tooltip, when the
  relevant pair isn't both non-null (queued-only, or predating this migration where `StartedAtUtc` is
  null but `EnqueuedAtUtc` was backfilled).

## Tests to update, not just add

- `TaskRunStoreTests.BeginRun_RecordsWhenTheWorkerActuallyClaimedTheRun...` — this asserted `BeginRun`
  writes `ClaimedAtUtc`; it now needs to assert `BeginRun` writes `StartedAtUtc`, and a new test covers
  `TryClaimNext` writing `ClaimedAtUtc` on `TaskRuns` (mirroring
  `RunExecutorIntegrationTests.AWorkerClaimingARun_RecordsTheClaimBetweenTheEnqueueAndTheEnd`, which
  needs the same retargeting — it currently checks the claim lands between enqueue and end via
  `BeginRun`'s write; it should check the claim lands at `TryClaimNext` instead).
- `RunMetricsStoreTests` — `AddRun`'s `queueWait`/`duration` parameters and the formula they build
  `EndedAtUtc` from need a third parameter (`enqueuedAt`/`claimedAt`/`startedAt` as three real, ordered
  points) so the queue-time and processing-time assertions are distinguishable from each other and from
  the single-figure version phase 72 tested. Apply the same discipline phase 72 used: check each new
  assertion fails against the old formula before trusting it passes for the right reason.
- Cross-engine coverage (`CrossEngineStateTests`) should cover the new migration for free, same as
  phase 72's did, since it uses the same `{{addcolumn}}` token plus a plain backfill `UPDATE`.

## What this phase should not do

- Add a queue-time/wait-time figure to the metrics card's percentiles. Still nobody's asked for it.
- Backfill `ClaimedAtUtc` for rows predating either this migration or phase 72's — there's still nothing
  to backfill it from.
- Touch `WorkQueue`'s own timestamp columns — they were already right; this phase is `TaskRuns` catching
  up to them, not the other way around.

## How to verify

Full suite green (`Category!=Integration` and `Category=Integration`), `tsc -b`/SPA build clean — same
bar phase 72 cleared. Confirm via a direct test that `TryClaimNext`'s new `TaskRuns` update and
`WorkQueue`'s existing one really do land in the same transaction (kill the connection mid-way in a test
if there's a way to, or at minimum assert both are written by inspecting the SQL/transaction object
rather than trusting two adjacent statements are atomic by proximity).
