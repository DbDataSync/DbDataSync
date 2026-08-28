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

### The Last-24-hours card

On the replication's Overview, where the mockup put it: runs, rows written, failures, and duration
percentiles with a sparkline. Real numbers or nothing — the rule phase 15 set and this phase keeps.

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

- **Lag.** It needs a definition before an implementation, and the definition is the hard part. See
  `planning/todo/run-lag.md`.
- **The health rollup** ("4 of 5 healthy"). It is a presentation of connection tests (phase 19) plus
  recent run outcomes, and inventing a third notion of "healthy" before those two are composed would
  be the third notion.
- **Retention**, above.
- **A cross-replication view.** The mockup's replications-list lag column is lag; the header rollup is
  health. Both are out.

## How to verify when built

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
