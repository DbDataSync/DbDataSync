# Phase 73 — `TaskRuns` timestamps, named for the moment each one records

**Status**: Complete.
**Plan reference**: `architecture/planning/done/task-run-timestamp-naming.md`

## The problem

Phase 72 added `TaskRuns.ClaimedAtUtc`, but wrote it inside `TaskRunStore.BeginRun` — the point a
worker has already claimed an item and is starting to execute it. `WorkQueue.ClaimedAtUtc` (unchanged,
written in `WorkQueueStore.TryClaimNext`) is the genuine claim moment, earlier. Same column name, two
tables, two different moments.

`TaskRuns.StartedAtUtc` had the same problem the other direction, and older: `WorkQueueStore.Enqueue`
wrote it at enqueue time, not at execution start. Phase 72 flagged this as known debt and left it (see
that phase's "What this phase did not build"); this phase is exactly that follow-up.

## What was built

### Three timestamps, three write sites

| Column          | Written by                     | Moment                         |
|-----------------|--------------------------------|--------------------------------|
| `EnqueuedAtUtc` | `WorkQueueStore.Enqueue`       | Work is queued                 |
| `ClaimedAtUtc`  | `WorkQueueStore.TryClaimNext`  | A worker takes it off the queue |
| `StartedAtUtc`  | `TaskRunStore.BeginRun`        | The worker starts executing it |
| `EndedAtUtc`    | `TaskRunStore.CompleteRun`     | The run finishes               |

`Enqueue` writes `EnqueuedAtUtc` and nothing else — a queued run has not been claimed and has not
started, and both columns say so. `BeginRun` writes `StartedAtUtc` in the same `UPDATE` that sets
`Status` and `Pid`, the one-statement atomicity phase 72 built, retargeted at the right column.

`TryClaimNext`'s claim step is now a transaction over two statements: the `WorkQueue` row's own claim,
and `UPDATE TaskRuns SET ClaimedAtUtc = $now WHERE RunId = $runId`. Both take the *same* `$now`
variable rather than two calls to `UtcNow`, which would have made the two tables disagree by a round
trip about a moment they are both describing. A lost race rolls back rather than falling through, so a
run never carries a claim time from a worker that did not claim it.

### The migration, and the one thing the phase doc did not anticipate

`EnqueuedAtUtc` is added with `{{addcolumn}}` and backfilled in the same script —
`UPDATE TaskRuns SET EnqueuedAtUtc = StartedAtUtc` — because every existing row's `StartedAtUtc`
already *was* its enqueue time. Unlike phase 72's `ClaimedAtUtc`, no history is lost.

The phase doc expected `StartedAtUtc` to keep its column definition untouched, with only the write site
moving. It could not: the column was declared `{{key}} NOT NULL` in the original `TaskRuns` DDL, and a
run that is queued but not yet started genuinely has no start to put there. Writing the enqueue time
into it to satisfy the constraint would have recreated the exact lie the phase exists to remove.

Relaxing the constraint in place is not available. SQLite has no `ALTER COLUMN` at all, so the options
were a full table rebuild (restating twenty-odd columns accumulated across a dozen migrations, and
keeping that restatement correct forever) or drop-and-re-add. Drop-and-re-add won, and it makes the
history honest rather than merely convenient: the old values were enqueue times, they are safe in
`EnqueuedAtUtc`, and leaving them under the name `StartedAtUtc` would have left every historical row
asserting a start that never happened. They go null, on precisely the reasoning phase 72 gave for not
backfilling `ClaimedAtUtc`.

That forced two further changes, both of which turned out to be improvements:

- **A `{{dropindex:Index:Table}}` token and `StateDialect.DropIndex`.** SQLite refuses to drop an
  indexed column, and `IX_TaskRuns_TaskName_StartedAt` was over this one. SQLite and Postgres name an
  index alone; SQL Server scopes index names to their table and insists on being told which. One
  virtual method, one override, the same shape as `AddColumn`.
- **The index follows the meaning.** It is now `IX_TaskRuns_TaskName_EnqueuedAt`, which is what the
  metrics window and the prune's recency ranking actually need — both ask when work was asked for.

### Consumers that switched columns

Phase 72's own audit was the checklist. Everywhere `StartedAtUtc` was serving as "when was this asked
for" now reads `EnqueuedAtUtc`:

- `TaskRunStore.PruneRuns`/`DoomedRuns` — the age cutoff and the per-mapping recency ranking.
- `RunMetricsStore` — the window selection in the totals, the processing-time query and the bucketing.
- `SchedulingEvaluator`'s input, via `TaskRunStore.GetLastPrimaryStartByMapping`, renamed
  `GetLastPrimaryEnqueueByMapping`. Due-ness measured from the *start* would let a run that queued
  behind a backlog push its own next run further out, so a busy replication would fall progressively
  further behind its own schedule.
- `GetRunHistory`/`GetMappingRunHistory`'s `ORDER BY`. Not on phase 72's list, and it had to move:
  `StartedAtUtc` is now null for every queued run, and ordering history by a column that is null for
  the newest rows sorts them wherever the engine feels like.
- `RunsPanel`'s row timestamp, and its column header (below).

Unaffected, as phase 72 found and as is still true: `GetRecentlyEndedRuns`, `ReadLastCompletedPass`,
`RunMonitorService`, and `RunTiming`'s per-stage figures, which the executor measures in process.

### Two figures, two names

- **Processing time** = `EndedAtUtc - StartedAtUtc`. `RunMetricsStore.ReadDurations` became
  `ReadProcessingTimes`, and `RunMetrics.DurationP50Ms`/`P95`/`Max` became `ProcessingP50Ms`/`P95`/`Max`
  — a judgment call the phase doc left open, decided for renaming. "Duration" is the name that let
  phase 72 measure from the wrong column for an entire phase without anyone noticing; a name that says
  which two timestamps it is between cannot fail the same way. The rename reaches the API contract and
  the SPA, which is four call sites, and the field is read by this project's own SPA only.
- **Queue time** = `StartedAtUtc - EnqueuedAtUtc`, per run, as `RunsPanel`'s processing-cell tooltip —
  the same place phase 72 put the old mistimed figure, recomputed from the corrected pair and labelled
  rather than left implicit. Still suppressed under a second, for phase 72's reason: a tooltip on every
  row saying nothing happened is how the rows where something did happen get overlooked.

Both null-guard the same way: no contribution to percentiles and no tooltip when the relevant pair is
not both non-null.

### In the SPA

The run history's first column said "Started" and showed the enqueue time. That was defensible while
nothing else recorded a start; it is not now that something does. It says **Queued** and reads
`enqueuedAtUtc`. The duration column is **Processing**, and the metrics card's percentiles are
**Processing time** (`data-testid` `metrics-processing`, updated in the golden-path spec with it).
`TaskRunRecord` in `types.ts` gains `enqueuedAtUtc`, and `startedAtUtc` becomes nullable — which the
compiler then required `clock()` to handle, catching the one place a null could have rendered
`Invalid Date`.

## How it was verified

- `TaskRunStoreTests.BeginRun_RecordsWhenTheRunActuallyStarted_NotWhenItWasQueued` — phase 72's test,
  retargeted from `ClaimedAtUtc` to `StartedAtUtc`, keeping its deliberate 20ms gap.
- `TaskRunStoreTests.TryClaimNext_RecordsTheClaimOnTheRun_BetweenTheEnqueueAndTheStart` — new, with a
  real gap on *both* sides of the claim, so it fails if the claim coincides with the enqueue or with
  the start. Those are the two directions this could be wrong, and phase 72 was wrong in one of them.
- `TaskRunStoreTests.TryClaimNext_LosingTheRace_LeavesNoClaimTimeOnTheRun` — the rollback path, raced
  through the public API rather than asserted by reading the SQL. Proximity is not atomicity, and the
  transaction is only load-bearing on the path where one of the two writes must not happen.
- `Enqueue_WritesAQueuedRow_BeforeBeginRunIsCalled` — extended: a queued run has an enqueue time, no
  claim time and no start time.
- `RunMetricsStoreTests.ProcessingTime_IsMeasuredFromTheStart_NotFromTheClaimBeforeIt` — new, and the
  reason `AddRun` gained a third point. One run queued, claimed a minute later, started five seconds
  after that, ran 100ms: `EndedAtUtc - EnqueuedAtUtc` is ~65,100ms, `- ClaimedAtUtc` is ~5,100ms,
  `- StartedAtUtc` is 100ms. Three formulas, three answers, so the assertion pins the pair rather than
  passing on a coincidence.
- `ProcessingTime_ExcludesTimeTheRunSpentWaitingInTheQueue` and
  `ARunThatNeverStarted_ContributesNoProcessingTime` — phase 72's two, carried forward; the second now
  covers both "never claimed" and "claimed but never started".
- `RunExecutorIntegrationTests.AWorkerRunningARun_RecordsEnqueueClaimStartAndEnd_InThatOrder` —
  retargeted. Phase 72's version asserted the claim landed between the enqueue and the end, which would
  still have passed with the claim written by `BeginRun`, because that was the only write site it could
  see. It now asserts all four in order, so every write site has to be on the path a worker takes.
- Cross-engine migration coverage came free, as the phase doc predicted: `CrossEngineStateTests` runs
  the full migration set against Postgres and SQL Server, and its 39 integration tests passed on the
  first run — including the `DROP COLUMN`/`DROP INDEX`/re-add sequence on all three engines.
- The negation was checked before the assertions were trusted: reverting `ReadProcessingTimes` to
  phase 72's `julianday(ClaimedAtUtc)` formula fails
  `ProcessingTime_IsMeasuredFromTheStart_NotFromTheClaimBeforeIt` and
  `ARunThatNeverStarted_ContributesNoProcessingTime`, and nothing else. Worth doing explicitly, for the
  reason phase 72 gave: a fixture change that makes the tests compile again can just as easily make
  them pass for the wrong reason.
- Full suite green — `Category!=Integration` 872 passed, `Category=Integration` 198 passed, `tsc -b`
  and the SPA build clean, 0 failures in either.

## Decisions

- **`ReadDurations` renamed rather than corrected in place** — see "Two figures, two names".
- **`TaskRunRecord`'s fields reordered to enqueue → claim → start → end**, which moved `ClaimedAtUtc`
  ahead of `StartedAtUtc` rather than only inserting the new field. It is a positional record read far
  more often than it is constructed (once, in `ReadRun`), and the order the four things happen in is
  the only order that will not have to be explained.
- **`TaskRunStore` gained a `RunColumns` constant.** Six queries selected the same 24-column list and
  `ReadRun` addressed it by ordinal; inserting a column shifted every ordinal, and six copies were six
  chances for one to drift by a column and hand the reader the wrong field silently. Named once
  instead.

## What this phase did not build

- A queue-time figure in the metrics card's percentiles. Phase 72 left the equivalent unbuilt; still
  nobody has asked for it, and it is now genuinely cheap to add from two columns that mean what they
  say.
- Backfilling `ClaimedAtUtc` for rows predating phase 72's migration, or `StartedAtUtc` for rows
  predating this one. There is nothing to backfill either from.
- Any change to `WorkQueue`'s own timestamp columns. They were right; this phase is `TaskRuns` catching
  up to them.

## Notes

`RunMetricsStore` still uses `julianday()`, which is SQLite-only. Phase 72 left it as found and so does
this one, for the same reason: it is a pre-existing gap against phase 63's multi-engine state store, and
fixing it here would mix an engine-portability change into a metric-definition change. It is worth
noting that this is now the *only* thing in the file that is engine-specific.

The `{{dropindex}}` token exists because of a nullability constraint discovered mid-implementation, not
because the phase set out to build dialect machinery — the phase doc explicitly expected none. It is
one method with one override, and the alternative was a SQLite table rebuild that would have had to be
maintained against every future `TaskRuns` migration.
