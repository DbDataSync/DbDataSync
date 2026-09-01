# Phase 85 — CDC lag: a real time-based staleness figure

**Status**: Not started.
**Plan reference**: `architecture/planning/done/cdc-lag-calculation.md`, which builds the CDC half of
`architecture/planning/todo/run-lag.md`'s still-blocked general design.

## The gap

Nothing computes a time-based staleness figure for any replication today. `run-lag.md` names CDC as one
of two mechanisms whose position genuinely converts to a time (`sys.fn_cdc_map_lsn_to_time`), but its
own next step ("build 32 and 34, then come back") has sat blocked on Postgres (34), still unbuilt. This
phase builds CDC's half concretely, without the general "reader declares a lag capability" abstraction
`run-lag.md` sketches — that still waits on a second real example.

The naive definition ("now minus our last applied change's commit time") is wrong: on a quiet source it
grows forever even when fully caught up. The correct comparison is against the source's own current
position's time, not wall-clock now.

## What to build

### `ChangeCheckHistory.SourceTimeUtc`

A new nullable column (`Migrations.cs`, `ChangeCheckHistory` — phase 75's table). Populated only for
`SourceKind = Cdc`; null for `ChangeTracking` rows (a version count has no time mapping). Computed in
`DriverChangeCounterSource.FetchAsync` (`ChangeCounterSource.cs`), in the existing CDC branch, right
after `MsSqlCdcCatalog.GetMaxLsnAsync` — the connection is already open and already switched to the
right database for exactly this reason (see that method's own doc comment on why `ChangeDatabase` has to
happen first). One more scalar query, `sys.fn_cdc_map_lsn_to_time(maxLsn)`, on the same round-trip.
`ChangePollingGate.RecordCheck` (`ChangePollingGate.cs:151`) gains one more column to write.

### The lag computation

A method (new or beside `ChangeCounters`/`ChangePollingGate` in `DataSync.Api.Services` — your call on
exact placement) taking a mapping and returning `TimeSpan?`:

1. Look up the mapping's own current position from `ChangeWatermarks` (phase 74, keyed by
   `MappingName`). Null (no watermark yet — first pass) → return null, nothing to compare.
2. Map that position to a time via `sys.fn_cdc_map_lsn_to_time` — mapping-specific, computed on demand,
   not cacheable in the shared history table.
3. Find the latest `ChangeCheckHistory.SourceTimeUtc` for the mapping's
   `(ConnectionName, SourceDatabase, Cdc)` group. If usable (exists, and fresh enough — pick a
   staleness threshold, implementation's call), use it. If not, fall back to a live fetch through
   `IChangeCounterSource` (extended to also return CDC's mapped source time) rather than reporting
   "unknown" — a fresh install or a replication whose schedule doesn't run the gate often shouldn't be
   stuck with no figure.
4. `lag = groupSourceTime - mappingAppliedTime`, clamped to zero (a mapping can legitimately read more
   recently than the group's last poll — that's "caught up," not negative lag).

### API

Expose the computed value so the (separate, not-yet-built) notifications latency trigger and any future
UI figure can both consume it. Match whatever this codebase's existing per-mapping-status API shape
already is rather than inventing a new endpoint pattern — check what `RunsController`/the replication-
detail endpoints already return alongside a mapping before adding a parallel one.

## What this phase should not do

- The general `run-lag.md` capability abstraction — waits for Postgres.
- The notifications latency trigger itself, or the worker-check-in half of that trigger (a different,
  unrelated heartbeat mechanism) — separate, unstarted phases.
- A UI card/column. Real, still wanted, cheap once this exists — not required to prove the calculation.
- Anything for Change Tracking, Postgres, Watermark, or BatchReload's "lag" — all different shapes, all
  still out of scope per `run-lag.md`'s own per-mechanism table.

## How to verify

- A test asserting `SourceTimeUtc` is populated for `Cdc` history rows and stays null for
  `ChangeTracking` ones.
- A test asserting a mapping caught up to the group's last known position reports zero, not negative,
  lag.
- A test simulating a quiet source (max LSN unchanged across ticks) with a mapping that has caught up,
  asserting lag does **not** grow with wall-clock time passing — the specific failure mode the naive
  definition would have had. This is the one test that most directly proves the design decision was
  right; don't skip it for the simpler zero-clamp test above.
- A test asserting the live-fetch fallback fires when no usable `ChangeCheckHistory` row exists for a
  group, and reuses the existing fail-open behavior on an unreachable source rather than throwing.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
