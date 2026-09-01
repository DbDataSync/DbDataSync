# Phase 75 — A lightweight polling gate for CDC and Change Tracking

**Status**: Not started. Depends on nothing from phase 74 (independent, additive), but sequence after
it if both are in flight, since phase 74 touches `SchedulerService`'s neighborhood conceptually and
it's easier to review one change to that area at a time.
**Plan reference**: `architecture/planning/done/watermark-key-and-lightweight-polling.md`

## The gap

`SchedulerService.Tick` decides what's due purely from local config and state
(`SchedulingEvaluator.IsDue`) — it never talks to a source system. Every due CDC or Change-Tracking
mapping gets its own `WorkItem`, its own connection, its own database-wide counter fetch
(`sys.fn_cdc_get_max_lsn()` / `CHANGE_TRACKING_CURRENT_VERSION()`), every cycle, even when nothing in
that source database changed. A database with 30 tracked tables pays that 30 times over for one answer
that's the same 30 times.

Both mechanisms already expose the one answer that matters, database-wide:

- Change Tracking: `CHANGE_TRACKING_CURRENT_VERSION()`, bumps on any write to any tracked table.
- CDC: `sys.fn_cdc_get_max_lsn()`, bumps on any write to any captured table.

## What to build

### A new state table

One row per `(ConnectionName, Database)` — the signal is database-wide, shared by every mapping and
every replication pointed at that database, so the key is neither `TaskName` nor `MappingName`. Store
the last-seen counter value and when it was checked.

### The gate, in `SchedulerService`

Before enqueuing this tick's otherwise-due CDC/Change-Tracking mappings, group them by
`(ConnectionName, Database)`. For each group: open the source connection, fetch the counter once,
compare to the stored value.

- Unchanged: skip enqueuing every mapping in that group this tick. Nothing about their own schedules
  changes — they're simply not due-enough-to-act-on this cycle, same as if the tick had found them not
  due at all.
- Changed, or no stored row yet: update the stored value, enqueue the group's mappings exactly as today
  (this gate decides whether to try, never what an individual mapping reads once dispatched — the
  per-mapping reader still does its own real incremental read independently).
- Unreachable source, or any error fetching the counter: **fail open** — skip the gate for that group
  and enqueue normally, exactly as `SchedulerService` behaves today. A source that's briefly down should
  degrade to today's per-mapping dispatch, not silently stop scheduling that replication. Log it, don't
  throw out of `Tick()`.

### The architectural shift, worth stating in the retrospective

`SchedulerService` does no source-system I/O today — this phase gives it its first. That's the
intended trade, not a side effect to minimize, but it changes the service's failure surface: a tick can
now be slowed or partially failed by an unreachable source, where before it only ever touched local
config and state. The fail-open behavior above is what keeps that from becoming a regression rather than
an optimization.

### Reader-kind scoping

Only mappings using `MsSqlChangeTrackingReader` or `MsSqlCdcReader` participate. Every other reader kind
(`WatermarkReader`, `TriggerAuditReader`, `BatchReloadReader`, `ScriptedQueryReader`) is scheduled
exactly as today — this phase doesn't touch their dispatch path at all.

## Explicitly out of scope, decided in planning

- **Trigger-audit.** No database-wide analogue exists — one shadow table per source table, not one
  shared table — and building one would create the exact lock-contention hot spot a trigger's existing
  per-transaction cost is already worth avoiding more of, not less. A softer, heuristic option (each
  engine's own async, non-locking modification-stats view, used only as a fail-open "definitely nothing
  changed" signal, never authoritative) is a real possibility but needs its own per-engine research and
  is left for a later, separate phase.
- Any change to what an individual reader does once dispatched — `MsSqlCdcReader`'s own per-table
  short-circuit (skip the `CHANGETABLE` scan if the stored LSN is already caught up) and
  `MsSqlChangeTrackingReader`'s missing equivalent are unrelated to this gate and untouched by it (the
  latter is a real, separate small gap worth its own tiny follow-up: `ReadChangesAsync` never compares
  `targetVersion` to `previousWatermark` before issuing the incremental query, unlike CDC's reader,
  which does. Note it in the retrospective; don't fix it here since this phase's scope is the shared
  gate, not per-reader efficiency).

## How to verify

- A test with two mappings on the same `(ConnectionName, Database)`, both due, asserting the counter is
  fetched once per tick, not twice.
- A test asserting an unchanged counter skips enqueueing for every mapping in the group.
- A test asserting a counter-fetch failure enqueues normally (fail-open), and does not throw out of
  `Tick()` or block unrelated replications' groups in the same tick.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
