# Run metrics and monitoring

The phase 15 mockups are full of numbers the system does not record: a **Last 24 hours** card (runs,
rows written, failures, median lag) with a sparkline, a **lag** column on the replications list, a
**4 of 5 healthy** rollup in the header, per-run **duration as an SLA-ish figure**, and reachability
dots on endpoints. All omitted, because a console showing an invented reading is worse than one
showing none.

Run **duration** is the exception and was kept: both timestamps are recorded, so it is real.

## What exists to build on

More than it looks. `TaskRuns` already stores, per run: kind, mapping, segment, status, start, end,
rows read, rows written and an error summary. So runs, rows written, failures and duration
distributions are all *queryable today* — they simply have no aggregate endpoint and no UI.

What genuinely does not exist is **lag** (no notion of source-side event time versus apply time) and
**health** (nothing tests a connection until phase 19, and nothing summarises across replications).

## The rough shape

- **An aggregate endpoint** over `TaskRuns` for a replication and a window: run count, rows,
  failures, duration percentiles. Straightforward, and delivers most of the Last-24-hours card.
- **A sparkline series** — the same query bucketed by time. Also straightforward.
- **Lag** needs a definition before it needs an implementation. Candidates: watermark age (how far
  behind the source's latest change the last successful pass got), or wall-clock since the last
  successful pass. The second is trivial and much less useful; the first is meaningful and only
  computable for readers that expose a comparable watermark — which excludes batch mode entirely.
- **Health rollup** is a presentation of connection tests (phase 19) plus recent run outcomes, and
  should probably wait until both exist rather than inventing a third notion of "healthy".

## Open questions

- What lag *means* for a batch or watermark replication, where there may be no source-side timestamp
  to compare against at all. It may simply not be defined for those, and the UI should then say so
  rather than show a dash that reads like zero.
- Retention. `TaskRuns` grows without bound today; an aggregate over "last 24 hours" is cheap, but a
  console that invites longer windows will want either an index or a rollup table.
- Whether any of this should push (SignalR already exists for live runs) or stay pull. Pull is the
  honest default for a dashboard nobody is staring at.
