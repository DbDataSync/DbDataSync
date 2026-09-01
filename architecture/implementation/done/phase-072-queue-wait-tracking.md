# Phase 72 — Queue-wait tracking, and duration redefined to exclude it

**Status**: Complete.
**Plan reference**: `architecture/planning/done/queue-wait-tracking.md`

## The gap

`TaskRuns.StartedAtUtc` is written by `WorkQueueStore.Enqueue`, at the moment work is queued and
before any worker exists to do it. `TaskRunStore.BeginRun` — the call a worker makes when it actually
claims an item and starts executing — flipped `Status` to `Running` and recorded nothing about when
that happened.

So the moment a run genuinely began was never written down anywhere, and every "duration" in the
product was computed as `EndedAtUtc - StartedAtUtc`: the run, *plus* however long it had waited for a
worker. Under a backlog that figure reports the backlog. A replication whose passes each took 200ms
but queued for a minute behind a busy sibling showed minute-long durations, and nothing in the product
could tell that apart from a slow source.

## What was built

### `TaskRuns.ClaimedAtUtc`

A new migration adds one nullable column. `BeginRun` writes `DateTimeOffset.UtcNow` into it in the
same `UPDATE` that sets `Status` and `Pid` — one statement, so a run cannot be `Running` without a
claim time or the reverse.

Null for a run that never reached `Running`: one still queued, or cancelled before a worker took it.
Also null for every row predating the column, which is not backfilled — there is nothing to backfill
*from*, and inventing a claim time would fabricate exactly the figure the column exists to measure.

`TaskRunRecord` gains `ClaimedAtUtc` positioned between `StartedAtUtc` and `EndedAtUtc`, because that
is the order the three things happen in and the record is read far more often than it is constructed
(once, in `ReadRun`). `RunsController` returns the record directly, so no DTO change was needed.

### Duration redefined

Both consumers now measure `EndedAtUtc - ClaimedAtUtc`:

- `RunMetricsStore.ReadDurations` — phase 36's p50/p95/max percentiles.
- `RunsPanel.tsx`'s duration column.

A run with no `ClaimedAtUtc` contributes no duration rather than a wrong one. In the SQL that is an
explicit `AND ClaimedAtUtc IS NOT NULL` rather than a reliance on `julianday(NULL)` propagating,
because the reader's `IsDBNull` guard would have swallowed the distinction silently; in the SPA it is
a dash, the same as a run that has not ended.

The metrics *window* is still selected on `StartedAtUtc`, deliberately: "which runs belong to the last
24 hours" is a question about when work was asked for, and it stays consistent with the totals and the
bucketing, which are selected the same way.

### Queue wait, exposed

As the two raw timestamps, not as a computed `QueueWaitMs` field — the open question the phase doc
left to implementation. Phase 71 exposed `PreviousWatermark`/`NewWatermark` the same way, raw rather
than as a precomputed delta, and matching it keeps one rule for this record rather than two. It also
keeps the derived figure defined in one place per consumer instead of in a field the API must keep in
step with the timestamps it was derived from.

In the UI, queue wait is the duration cell's tooltip in `RunsPanel`. The planning doc suggested phase
62's expandable per-run timing detail, but that row only opens for a traced or failed run — and queue
wait is interesting on precisely the rows that are neither. The tooltip is suppressed under a second,
because the ordinary case is an idle worker taking the item at once, and a tooltip on every row saying
nothing happened is how the rows where something *did* happen get overlooked.

## The audit, beyond the two named consumers

The phase doc asked for an audit rather than an assumption. Every reference to `EndedAtUtc` across
`src/` and `tests/` was checked, since a duration cannot be computed without it. Every other use is a
null-check or an ordering, not a subtraction:

- `TaskRunStore.PruneRuns` / `DoomedRuns` — `EndedAtUtc IS NOT NULL` as "this run finished", and ranks
  by `StartedAtUtc`. Retention is about when work was asked for; unchanged, correctly.
- `TaskRunStore.GetRecentlyEndedRuns` — filters on `EndedAtUtc >= $since`. No subtraction.
- `RunMetricsStore.ReadLastCompletedPass` — `MAX(EndedAtUtc)`. A point in time, not a span.
- `RunMonitorService` — comments about the above filter; computes nothing.
- `SchedulingEvaluator` — due-ness from the last run's `StartedAtUtc`. This is the one place the
  enqueue time is the *right* answer: "has it been N seconds since we last asked for this" is a
  question about when it was asked for.
- `RunsPanel`'s `clock(r.startedAtUtc)` — displays the enqueue time as the run's timestamp, which is
  still the honest label for when a run entered the system.

So the two named consumers were the two that existed. `RunTiming`'s per-stage durations (phase 59) are
measured in-process by the executor and never involved either column.

## How it was verified

- `TaskRunStoreTests.BeginRun_RecordsWhenTheWorkerActuallyClaimedTheRun_NotWhenItWasQueued` — a
  deliberate 20ms sleep between enqueue and `BeginRun`, asserting the gap is visible and that
  `StartedAtUtc` is left alone. Fails if `BeginRun` stops writing the column or writes the enqueue
  time into it.
- `Enqueue_WritesAQueuedRow_BeforeBeginRunIsCalled` extended: a queued run has no claim time.
- `RunMetricsStoreTests.Duration_ExcludesTimeTheRunSpentWaitingInTheQueue` — two runs that each
  executed 100ms, one of which queued for a minute first. Under the old formula the max would have
  been ~60,100ms; both p50 and max now read 100ms, so the assertion is that the wait is absent from
  the figure rather than merely reduced.
- `RunMetricsStoreTests.ARunThatWasNeverClaimed_ContributesNoDuration` — the pre-existing-row and
  cancelled-run case.
- `RunExecutorIntegrationTests.AWorkerClaimingARun_RecordsTheClaimBetweenTheEnqueueAndTheEnd` — the
  same property through the real executor rather than a direct `BeginRun` call, so the assertion
  covers the write site being on the path a worker actually takes. Ordering only; the magnitudes there
  are microseconds and a threshold on them would assert the clock rather than the code.
- Cross-engine migration coverage came free: `CrossEngineStateTests` already runs the full migration
  set against Postgres and SQL Server, and the new statement uses `{{addcolumn}}`/`{{text}}`.
- Full suite green — `Category!=Integration` 869 passed, `Category=Integration` 198 passed, `tsc -b`
  and the SPA build clean, 0 failures in either.
- The duration test was checked against its own negation: reverting `ReadDurations` to
  `julianday(StartedAtUtc)` makes it fail. Worth doing explicitly, because the fixture change that
  made these tests compile again could easily have made them pass for the wrong reason.

## The test that was proving the old formula

`RunMetricsStoreTests.AddRun` built each row's `EndedAtUtc` as `startedAt + duration`. With no
`ClaimedAtUtc` written, every duration assertion in that file would have gone null and failed — and,
worse, the naive fix (writing `ClaimedAtUtc = startedAt`) would have made them all pass again while
proving nothing, because a zero queue wait makes the old and new formulas identical.

So the helper gained a `queueWait` parameter and now builds `EndedAtUtc` as
`startedAt + queueWait + duration`. The existing tests pass a zero wait and keep asserting exactly what
they asserted before; the new test passes a real one. That is what makes the two formulas
distinguishable by the suite at all.

## What this phase did not build

- **Renaming `StartedAtUtc`.** Its meaning has not changed — it always meant "enqueued at" — only what
  is computed from it. A rename touches every reader, the prune's ranking, the scheduler, the metrics
  window and the SPA type. Recorded as a known naming debt in `implementation-plan.md`'s backlog
  rather than left implicit.
- Any change to `WorkQueue`'s own `EnqueuedAtUtc`/`ClaimedAtUtc`, which were already correct and are
  untouched. This phase gave `TaskRuns` the honesty `WorkQueue` already had.
- Backfilling `ClaimedAtUtc` for historical rows.
- A queue-wait figure in the metrics card. Queue wait is now per-run and visible; aggregating it into
  percentiles beside the duration ones is a reasonable follow-on, and one nobody has asked for.

## Notes

`RunMetricsStore.ReadDurations` uses `julianday()`, which is SQLite-only, and this phase left that as
it found it — the column changed, the shape did not. It is a pre-existing gap against phase 63's
multi-engine state store rather than anything introduced here, and widening this phase to fix it would
have mixed an engine-portability change into a metric-definition change.
