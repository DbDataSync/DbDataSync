# Phase 60 — Pruning the task run log, by age and/or per-mapping row count

**Status**: Complete
**Plan reference**: `architecture/planning/done/task-run-log-pruning.md`

## What this covers

A config-file retention policy for `TaskRuns` (and its cascaded `Logs`), enforced by a new periodic
background service in the API. Two independent, optional caps — age in days and a per-mapping row
count — with defaults applied when unset.

## 1. Config

`ApiOptions` gained `RunRetentionDays` (default 90), `RunRetentionMaxPerMapping` (default 1,000) and
`RunPruningInterval` (default hourly, settable as `RunPruningIntervalMinutes`), all read from the
existing `DataSync` section.

## 2. The prune query

`TaskRunStore.PruneRuns(TimeSpan? maxAge, int? maxPerMapping)`. One `ROW_NUMBER() OVER (PARTITION BY
TaskName, MappingName ORDER BY StartedAtUtc DESC)` subquery expresses both caps; a row failing either
is pruned. `EndedAtUtc IS NOT NULL` excludes anything in flight. `Logs` is deleted through the same
subquery in the same transaction.

## 3. The background service

`RunPruningService : BackgroundService`, alongside `SchedulerService`/`RunMonitorService`, on a
`PeriodicTimer`. Runs once immediately, then on the interval.

## What this phase does not build

- Verification result pruning — see the retrospective; the orphan is real and flagged.
- A UI settings surface for retention — config-file only, per the resolved decision.
- Failed-run protection (keeping failures longer than successes).

---

# Retrospective

Small and mostly mechanical. The decisions that took thought were all about what a *default* should be
and what a *typo* should do, which for a feature whose job is deleting things matters more than the
mechanism does.

## Both caps in one statement, not two passes

The obvious implementation is two deletes: one by age, one by count. One statement is better here for
a reason beyond the extra scan — two deletes would make "which cap removed this run" a question with
an answer, and an answer invites something to depend on it. A row is either within retention or it is
not.

The `ROW_NUMBER()` window does the per-mapping ranking and the age predicate sits beside it, so the
subquery names exactly the doomed set and both deletes use it.

A null cap is expressed as `$param IS NULL OR …` rather than by building different SQL. Two statement
shapes would be two things to keep correct, and this runs once an hour rather than in any hot path.

## Unset is a policy; zero is the operator

Two silences, needing opposite treatments:

- **Nothing configured** applies the defaults rather than meaning "no limit". A state database that
  only ever grows is not a policy anyone chose — it is what happens when nobody chooses one, and the
  cost lands months later on whoever is trying to work out why the API got slow.
- **An explicit `0`** means "keep everything", not "keep nothing". This is the one place a typo can
  destroy data: a `0` read as a limit would empty the run history on the first sweep. Between two
  readings of the same mistake, the recoverable one has to win. Anything unparseable is treated the
  same way, for the same reason.

## In-flight runs, and why the predicate is `EndedAtUtc`

The phase doc says to exclude rows still `Running`. `EndedAtUtc IS NOT NULL` is used instead, which is
strictly wider: it also covers `Queued`, `Claimed`, and anything stranded mid-flight by a killed
worker. Deleting the row underneath a worker that is about to write to it would turn a slow pass into
a lost one, and a status check would have missed the stranded cases — which are exactly the rows most
likely to be old.

## The test that found a real behaviour, in the queue

Four of the pruning tests failed on the first run, all showing one run where the test had created ten.
`WorkQueueStore.Enqueue` deduplicates on the in-flight unique index: enqueuing the same
`(task, kind, mapping, segment)` while one is pending returns the existing item's RunId rather than
inserting a second row.

That is correct for the queue — it is what stops a scheduler tick double-queueing a mapping — and the
tests were wrong to assume otherwise. Each test run now gets a distinct segment label, and the helper
says why, because the next person writing a `TaskRuns` fixture will hit the same thing.

## Decisions the phase doc left open

- **`RunRetentionDays` = 90.** Long enough to answer "was this mapping always this slow" across a
  quarter, which is the longest question run history is actually asked.
- **`RunRetentionMaxPerMapping` = 1,000.** More than any run-history view pages through, and small
  enough that a mapping running every fifteen seconds does not carry a year of rows to answer a
  question about the last few days.
- **Hourly, and configurable in minutes.** The doc offered hourly, daily or configurable; it is both.
  Coarse because nothing about retention is time-sensitive — a run that should have gone an hour ago
  costs a few kilobytes — and a frequent sweep would be contention with the writers that matter for a
  benefit nobody could observe. Configurable because a deployment with a much shorter retention wants
  a proportionally shorter sweep, and that is one line.
- **One immediate sweep at startup, then the interval.** An API that has just come back after a week
  down has a week of runs past their retention already; waiting an hour to start is an hour of holding
  rows that were expired before the process launched.
- **Pruning failures are logged and swallowed.** A locked or briefly unavailable state database is a
  reason to try again next hour, not to take down a background service the process never restarts.
  Retention is the least urgent thing this process does.
- **Both caps off logs once at startup.** An operator who turned retention off should see that
  reflected; one who thinks they configured it and did not should find out from a log line rather than
  from a disk.

## The orphan this phase creates

Per the phase doc, and worth restating because it is now real rather than hypothetical: a verification
run's `TaskRuns` row is pruned by this, and its parquet result file and `VerificationResultStore` index
entry are not. Phase 43's retrospective already named "nothing deletes a result today" as a gap; this
phase makes it slightly worse by removing the run that pointed at it. Flagged in the README beside the
retention settings rather than left to be discovered.

## Verification

- `RunPruningTests` (9) — neither cap pruning nothing; the age cap keeping recent runs; the count cap
  keeping exactly the most recent N; **the count cap being per mapping**, with a quiet mapping
  surviving beside a noisy one being pruned; per task as well as per mapping; an unfinished run never
  pruned whatever its age, covering both Running and Queued; log lines going with their run and
  staying with the runs that survived; both caps applying independently in both directions; and
  pruning being idempotent.
- `RunRetentionOptionsTests` (8) — unset meaning the defaults rather than unlimited; configured values
  used; `0`, a negative, and nonsense all meaning "no cap"; the two caps independent; and the interval
  defaulting to hourly, being configurable, and falling back rather than producing a zero-length timer
  that `PeriodicTimer` would reject.
- Full suite green: 810 unit, 153 integration.

## Open questions

- ~~**Exact default values.**~~ 90 days and 1,000 per mapping, for the reasons above.
- ~~**Pruning interval.**~~ Hourly, configurable in minutes.
- **Verification results are still never pruned** — the orphan above. It wants its own small phase:
  the parquet files and the index entry, with the same two-cap shape this one established.
- **`RunPruningService` has no test of its own.** The pruning it performs and the options it reads are
  both covered directly; the loop between them — timer, immediate first sweep, swallowed failure — is
  not, matching how `SchedulerService` and `RunMonitorService` are (or are not) tested today.
