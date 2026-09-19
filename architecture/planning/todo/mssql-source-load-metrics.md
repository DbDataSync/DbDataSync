# MSSQL source load metrics — sampling CPU/memory/IO over time for later analysis

**Status: proposal, not agreed. Raw thought, not yet designed.**

## The problem

We have no visibility into system load on MSSQL source servers over time. Operators (and whoever
analyzes this later) want to be able to see where CPU, memory, IO, etc. were at over the life of a
replication — not because this application needs to act on it, but so someone can correlate load against
run history, replication schedules, or incidents after the fact.

## Scope, deliberately narrow

This application's job stops at collecting and storing samples. It does **not** need to:
- interpret the numbers, alert on them, or render them,
- do anything beyond what's needed to get clean data points into the state db.

The consumer is a separate analysis this app won't provide.

## Constraints

- **Low overhead on the source.** Whatever we query has to be cheap and safe to run repeatedly against
  a production source — this is exactly the kind of thing that must not become the load problem it's
  trying to observe. Likely means querying SQL Server's own in-memory DMVs
  (`sys.dm_os_performance_counters`, `sys.dm_os_sys_info` for CPU, `sys.dm_os_process_memory`,
  `sys.dm_io_virtual_file_stats` for IO, `sys.dm_os_wait_stats`) rather than anything that scans user
  data or holds locks. None of this is used anywhere in this codebase yet — no existing DMV plumbing to
  build on.
- **Sampled over time, stored in the state db.** New table(s), one row per sample per connection
  (or per metric per sample — undecided), timestamped. Needs a retention story eventually (see
  `architecture/planning/done/run-metrics.md`'s own open question on `TaskRuns` growing unbounded — same
  shape of problem here, probably worse given sampling cadence vs. run cadence).
- **Sampling cadence** is itself an open question — probably a scheduled background tick similar to
  `SchedulerService`'s existing periodic pattern, but a load sampler is arguably per-*connection*
  (source), not per-mapping or per-replication, since the thing being measured is the server, not any
  one table.

## Open questions

- Which DMVs, exactly, and which specific counters/columns out of them — needs someone to actually decide
  what "memory, CPU, IO" means in SQL Server DMV terms and confirm the query shape is safe to poll
  repeatedly.
- Per-connection or per-instance? Two mappings against the same source shouldn't sample twice.
- Cadence, and whether it's configurable per connection or global.
- Storage shape: wide table (one column per metric) vs. narrow (metric name/value rows) — narrow is more
  flexible if the metric set grows, wide is cheaper to query. Given the consumer is external analysis,
  narrow (long/tidy) probably serves that better.
- Retention/pruning policy, mirroring whatever gets decided for `TaskRuns`/run metrics generally.
- Whether this generalizes to other engines later, or stays MSSQL-only for now — framed as MSSQL-only
  per the request, but worth naming explicitly as a scope decision rather than an oversight.
- No read path (query endpoint, export, etc.) is in scope here per the stated goal — worth confirming
  that's really true once this gets designed, since "stored in the state db" with literally no way to
  get it back out is an unusual end state.
