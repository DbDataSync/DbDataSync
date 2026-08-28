# Run metrics

**Status: resolved 2026-08-27 — see Outcome at the end.** Split out of `run-metrics-and-monitoring.md`;
the lag half is `planning/todo/run-lag.md`.

The phase 15 mockups are full of numbers the system does not record: a **Last 24 hours** card (runs,
rows written, failures) with a sparkline, per-run **duration as an SLA-ish figure**, and a
**4 of 5 healthy** rollup in the header. All omitted, because a console showing an invented reading is
worse than one showing none.

Run **duration** is the exception and was kept: both timestamps are recorded, so it is real.

## What exists to build on

More than it looks. `TaskRuns` already stores, per run: kind, mapping, segment, status, start, end,
rows read, rows written and an error summary. So runs, rows written, failures and duration
distributions are all *queryable today* — they simply have no aggregate endpoint and no UI.

## The rough shape

- **An aggregate endpoint** over `TaskRuns` for a replication and a window: run count, rows, failures,
  duration percentiles. Straightforward, and delivers most of the Last-24-hours card.
- **A sparkline series** — the same query bucketed by time. Also straightforward.
- **Health rollup** is a presentation of connection tests (phase 19) plus recent run outcomes, and
  should probably wait until both exist rather than inventing a third notion of "healthy".

## Open questions

- Retention. `TaskRuns` grows without bound today; an aggregate over "last 24 hours" is cheap, but a
  console that invites longer windows will want either an index or a rollup table.
- Whether any of this should push (SignalR already exists for live runs) or stay pull. Pull is the
  honest default for a dashboard nobody is staring at.

---

# Outcome — resolved 2026-08-27

Agreed, as `implementation/todo/phase-036-run-metrics.md`. The survey above is what made it small: the
data is already stored, so this is an endpoint and a card rather than instrumentation.

Decisions the phase makes on this doc's open questions:

- **An index, not a rollup table.** `(TaskName, StartedAtUtc)`, and nothing more, because a rollup is a
  second copy of the truth that needs maintaining and there is no evidence yet that the index is
  insufficient. Measure first — the rule phase 14's benchmark work established.
- **Pull.** SignalR exists and would be easy to reach for, but pull is right for a dashboard nobody is
  staring at, and the live-run hub already covers the case where someone is.
- **Retention is out.** Deleting a run deletes its logs, which is what an operator goes looking for
  after an incident. How long to keep run history is a policy question, not an implementation one.

One thing the phase adds that this doc did not raise: **duration is presented as a distribution, not as
a single figure.** The mockup's single number reads like an SLA the system is measuring itself against.
It is not — it is what happened — and p50/p95 say so honestly.

The health rollup stays out, for the reason recorded here: it is a composition of phase 19's connection
tests and recent run outcomes, and inventing a third notion of "healthy" ahead of composing those two
would be the third notion.
