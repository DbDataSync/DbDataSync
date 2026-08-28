# Phase 36 — Run metrics (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/run-metrics.md`, split out of
`run-metrics-and-monitoring.md`. The lag half stays in `planning/todo/run-lag.md`, where it belongs
until someone decides what lag *means*.

## The finding that makes this small

The mockups are full of numbers the system does not record, which is why phase 15 omitted all of them.
But the planning doc's survey found that **most of them are already stored**:

`TaskRuns` holds, per run: kind, mapping, segment, status, start, end, rows read, rows written and an
error summary. So run counts, rows written, failure counts and duration distributions are **queryable
today**. They have no aggregate endpoint and no UI, and that is the whole gap.

What genuinely does not exist is *lag* (no notion of source event time versus apply time) and *health*
(a rollup across replications). Neither is in this phase.

## What this builds

### An aggregate endpoint

```
GET /api/replications/{name}/metrics?window=24h
```

returning, over `TaskRuns` for that replication and window: run count, rows read, rows written, failure
count, and duration percentiles (p50, p95, max). Plus the same query bucketed by time for the
sparkline — one endpoint, two shapes, because two round trips for one card is two round trips.

Straightforward SQL over a table that already has the data. The only design question is indexing, below.

### Time since the last completed pass

**Added 2026-08-28**, from `planning/todo/run-lag.md`'s decision that *both* of the mockup's lag numbers
are wanted rather than one instead of the other.

This is the trivial one: wall-clock since the most recent successful run finished, straight out of
`TaskRuns`. It answers **"is this replication still running at all"**, which is a different question
from "how stale is the data" and is worth having on its own.

It lands here rather than in a phase of its own because it is one column of one query on a card this
phase is already building, and because it would be an almost-empty phase otherwise.

**It is not called "lag" in the UI**, and that matters. A replication that ran two minutes ago and found
nothing looks identical to one that ran two minutes ago and is an hour behind — so labelling this "lag"
would tell the operator something false. *Last pass* is what it is.

The staleness number — how far behind the source the applied data is — stays in `run-lag.md`, blocked on
phases 32 and 34 for the reason recorded there: it is a version count for Change Tracking, a real
duration for CDC, bytes for a Postgres slot, and undefined for batch reload, so it wants two working
examples before a design.

### The Last-24-hours card

On the replication's Overview, where the mockup put it: runs, rows written, failures, duration
percentiles with a sparkline, and time since the last completed pass. Real numbers or nothing — the
rule phase 15 set and this phase keeps.

**Duration is presented as a distribution, not as an SLA.** The mockup shows it as a single figure,
which reads like a target the system is measuring itself against. It is not; it is what happened. p50
and p95 say that honestly and a single number does not.

### A window the operator picks

24 hours is the default because it is what the mockup shows. The endpoint takes the window as a
parameter, and the card offers 1h / 24h / 7d — because "is it failing *now*" and "did it fail this
week" are different questions and a fixed window answers only one.

## Retention and indexing

`TaskRuns` grows without bound today. An aggregate over 24 hours is cheap on a small table and is a
scan on a large one, and a console that offers 7d invites the larger scan.

So this phase adds **an index on `(TaskName, StartedAtUtc)`** and nothing else. Not a rollup table:
that is a second copy of the truth, it needs maintaining, and there is no evidence yet that the index
is insufficient. Measure before adding one — the same rule phase 14's benchmark work established, and
`tools/benchmarks` exists.

Retention itself — deleting old runs — is deliberately **not** in this phase. Deleting a run deletes
its logs, which is the thing an operator goes looking for after an incident, and "how long do we keep
run history" is a policy question rather than an implementation one.

## Pull, not push

SignalR already exists for live runs and it would be easy to push metrics down it. Pull is the honest
default for a dashboard nobody is staring at, and the live-run hub already covers the case where
someone *is* — they are watching a run, not a 24-hour aggregate.

## What this phase does not build

- **Data staleness** — how far behind the source the applied data is. It needs a definition before an
  implementation, and the definition is the hard part. Time-since-last-pass *is* built here, above; the
  two are different numbers. See `planning/todo/run-lag.md`.
- **The health rollup** ("4 of 5 healthy"). It is a presentation of connection tests (phase 19) plus
  recent run outcomes, and inventing a third notion of "healthy" before those two are composed would
  be the third notion.
- **Retention**, above.
- **A cross-replication view.** The mockup's replications-list lag column is lag; the header rollup is
  health. Both are out.

## How to verify when built

- Time since the last **completed** pass, asserted against a replication whose most recent run failed —
  the case where "last run" and "last successful run" differ, and the one that makes the number mean
  something.
- Unit tests on the aggregation over a seeded `TaskRuns`: counts, sums, percentiles at known
  distributions, and an empty window returning zeroes rather than nulls — a dash that reads like zero
  is exactly the invented reading phase 15 refused.
- Bucketing at a window boundary, which is where off-by-one lives.
- `Category=Integration`: run a replication, hit the endpoint, see the run.
- Playwright: the card showing real figures after a run, and the window selector changing them.
- A test that the index is used — or at least that the query plan is not a scan at a size worth caring
  about.
- Full suite green.

## Open questions

- **Percentiles in SQLite.** No `PERCENTILE_CONT`. Either order-and-offset in SQL, or pull durations
  and compute in C#. The second is simpler and fine at 24 hours; the first matters at 7 days. Worth
  measuring rather than assuming.
- **Backfill runs in the same figures?** A reload moving ten million rows next to incremental passes
  moving hundreds will dominate every total. Probably: split by `RunKind`, since the two answer
  different questions.

---

# Retrospective

Built as planned, and it was as small as the plan said, for the reason the plan said: `TaskRuns` has
held every one of these figures since phase 5. What was missing was the query, not the data.

## An empty window is zeroes, a running pass is null

The two look alike and are not. Nothing ran in the last hour → **zero** passes, zero rows: that is a
fact about the window. A pass that has not finished → **no duration**: there is no number yet, and
zero would claim it took no time.

SQL makes this easy to get wrong, because `SUM` over no rows is `NULL`. Coalescing those to zero and
leaving the duration percentiles null is the distinction phase 15 refused to blur when it shipped no
numbers rather than placeholder ones.

## Last completed pass is not bounded by the window

It is at its most useful exactly when it is *older* than the window — that is the case where a
replication has stopped, which is the thing the number exists to reveal. A query scoped to the window
could never report it, so it is a separate query with no time bound at all.

And it is **not called lag**, in the UI or in the code. A replication that ran two minutes ago and
found nothing looks identical to one that ran two minutes ago and is an hour behind. Calling this
"lag" would tell an operator something false about the second case. Data staleness stays in
`planning/todo/run-lag.md`, where it waits on a definition rather than an implementation.

## A percentile test that could not tell itself from an average

The first version seeded nineteen 100ms runs and one 10s run and asserted p95 was 10s. It is not:
nearest-rank p95 of twenty values is the nineteenth, and with a single outlier the honest answer is
100ms — 95% of runs really did take 100ms. The test was asserting a bug.

Eighteen fast and two slow makes the assertion mean what it was meant to: the mean is about 1.1s and
neither percentile is near it, so an implementation that quietly averaged would fail.

## Percentiles in C#, and why

The plan left this open. SQLite has no `PERCENTILE_CONT`, so the choice was ordering in SQL with three
`LIMIT 1 OFFSET n` queries, or selecting the durations and computing in C#. The second: one column of
one index scan beats three scans of the same index, the result is exact rather than interpolated, and
at the size this is asked about a week of durations is a small list of doubles. `tools/benchmarks` is
where to revisit it if that stops being true.

Nearest-rank rather than interpolated, deliberately: it names a duration a run actually took, rather
than a number between two that no run did.

## The index, asserted rather than assumed

The plan asked for a test that the query plan is not a scan. `ExplainTotals` runs
`EXPLAIN QUERY PLAN` against **the same string** the aggregate executes — a copy of the SQL in the
test would be a copy that could drift — and the test seeds two thousand rows first, so the planner is
choosing rather than shrugging at an empty table. SQLite reports a seek on
`IX_TaskRuns_TaskName_StartedAt`, and the test fails if any line begins `SCAN`.

No rollup table, per the plan: that is a second copy of the truth, it needs maintaining, and there is
now evidence that the index suffices.

## Backfills are a different question

The plan's open question guessed "probably split by RunKind", and that is right for a blunt reason: a
reload moving ten million rows next to incremental passes moving hundreds does not skew the total, it
*is* the total. The endpoint takes the kind and the card asks for Primary — the one an operator means
by "is this working". Asking for Backfill, or for both, is a query-string away.

## Verification

- `RunMetricsStoreTests` (10) — an empty window reporting zeroes but null durations; counts and sums;
  percentiles against a distribution where they differ from the average; a running pass counted with
  no duration; last-completed-pass ignoring a failed later run and found outside the window; run kinds
  kept apart; another replication's runs not counted; bucketing with an inclusive left edge and an
  exclusive right one; and the query plan seeking the index.
- `PreviewIntegrationTests` (+2, `Category=Integration`) — the endpoint before and after a real run,
  the window as a real filter, a backfill not folded into the incremental figures, and an
  unrecognised window refused rather than silently defaulted.
- Playwright 21 — the card showing real figures after the suite's runs, the duration as a
  distribution, the sparkline, "Last completed pass" rather than "lag", and the window selector
  changing what is asked for.
- Full .NET suite green: 565 tests. Playwright: 23 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions

- ~~**Percentiles in SQLite.**~~ Computed in C# from one ordered column — see above.
- ~~**Backfill runs in the same figures?**~~ No; split by kind, defaulting to Primary.
- **Retention** is still not built, and still deliberately. Deleting a run deletes its logs, which is
  what an operator goes looking for after an incident, and how long to keep run history is a policy
  question. The index makes the absence survivable for longer, which is the point of doing it first.
- **A cross-replication rollup** is still out. The replications list wants a column and the header
  wants "4 of 5 healthy"; both are compositions of this plus connection tests, and inventing a third
  notion of healthy before composing those two would be the third notion.
