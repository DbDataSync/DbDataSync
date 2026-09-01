# CDC lag: a real time-based staleness figure, reusing the polling gate's audit trail

**Status: resolved — ready for an implementation phase doc.**

## Where this came from

`run-lag.md`'s "watermark age" half — "time difference between source and target applied data" — has
always been blocked on needing a reader whose position converts to a real time. CDC is one of the two
mechanisms named there as capable of it: `sys.fn_cdc_map_lsn_to_time` turns an LSN into the wall-clock
time its transaction committed, so the difference between two mapped times is a genuine duration, not a
version count or a byte size. `run-lag.md`'s "next step" was "build phases 32 and 34, then come back" —
32 (CDC) is done; 34 (Postgres logical replication) is not, and this phase doesn't wait for it. It builds
CDC's half now, concretely, rather than the general "reader declares a lag capability" abstraction
`run-lag.md` sketches — that abstraction should wait for a second real example (Postgres) to exist before
being designed, per that doc's own reasoning ("both will have learned something about what their
position actually compares against").

The notifications plan's latency trigger (a follow-on, not yet built) needs exactly this calculation.
This phase builds the calculation itself, reusable by that trigger and by a future UI figure, without
committing to either consumer yet.

## The naive definition is wrong, and why

"Now minus the time our last applied change committed" looks right and isn't: on a quiet source, that
figure grows forever even when a replication is perfectly caught up, because there's nothing new to
catch up *to*. The correct comparison is our applied position's time against **the source's own current
position's time** — if both map to the same moment, lag is genuinely zero, whether that moment was one
second or one hour ago.

## Design: reuse `ChangeCheckHistory`, per the direction given

`ChangeCheckHistory` (phase 75) already fetches the database-wide max LSN on every gate tick and stores
it with a `CheckedAtUtc` timestamp — but `CheckedAtUtc` is *when we polled*, not *when that LSN's
transaction actually committed at the source*. Those differ, usually by very little, but "very little"
isn't the same as zero, and the whole point of this figure is to be a real duration.

**Add a nullable `SourceTimeUtc` column to `ChangeCheckHistory`.** Populated only for `SourceKind = Cdc`
(null for Change Tracking rows — a version count has no time mapping, consistent with `run-lag.md`'s
per-mechanism table). Computed in `DriverChangeCounterSource.FetchAsync`
(`ChangeCounterSource.cs`), in the same branch that already calls
`MsSqlCdcCatalog.GetMaxLsnAsync` — the connection is already open, already switched to the right
database (`connection.ChangeDatabase(sourceDatabase)`, already done for exactly this reason per that
method's own doc comment); one more call, `sys.fn_cdc_map_lsn_to_time(maxLsn)`, on the same round-trip,
costs nothing extra in connections opened. `ChangePollingGate.RecordCheck` (`ChangePollingGate.cs:151`)
already writes the row; it gains one more column to write.

**Per-mapping lag** = (latest `ChangeCheckHistory.SourceTimeUtc` for that mapping's
`(ConnectionName, SourceDatabase, Cdc)` group) minus (the mapped time of that mapping's own current
`ChangeWatermarks` position, via the same `fn_cdc_map_lsn_to_time` call, computed on demand — mapping-
specific, so not something the shared history table can cache for it). Clamp negative results to zero:
a mapping can legitimately read more recently than the gate's last poll for its group, which would
otherwise show as impossible negative lag rather than "caught up."

**Fallback when there's no usable history**: a replication that's never had the gate run for its group
(no `ChangeCheckHistory` row yet, or a stale one beyond some freshness threshold — implementation's call
on the exact threshold) falls back to a live fetch through the same `IChangeCounterSource` shape,
extended to also return CDC's mapped source time. This keeps the common case cheap (reuse the gate's
already-scheduled fetch) without making a fresh install or a Periodic-mode replication (which may not
run the gate on the same cadence — confirm this before assuming it always has a recent row) report "lag
unknown" indefinitely.

## What this phase should build

- The `ChangeCheckHistory.SourceTimeUtc` column and its population, above.
- A lag-computation method (`DataSync.Api.Services`, likely beside `ChangePollingGate`/
  `ChangeCounterSource` given the shared machinery) taking a mapping and returning a `TimeSpan?` — null
  when the mapping isn't CDC, has no applied watermark yet (first pass), or the source declines to state
  a position (`fn_cdc_get_max_lsn()` before the capture job has run — same null-is-not-quiet handling
  `ChangeCounterSource`'s doc comments already establish for this exact function).
- Expose it via API, so the (separate, not-yet-built) notifications latency trigger and any future UI
  figure can both consume the same computation rather than each reimplementing it.

## What this phase should not do

- The general "reader declares a lag capability, with its unit" abstraction from `run-lag.md` — stays
  deferred until Postgres (phase 34) gives it a second real example.
- The notifications latency trigger itself — a separate phase, this one only builds what it needs.
- A UI card/column — `run-lag.md`'s original mockup ask, real and still wanted, but not required to prove
  this calculation works. A cheap follow-on once the number exists and is exposed via API.
- Anything for Change Tracking's version-count "lag," Postgres's slot-byte "lag," or Watermark mode's
  timestamp-column case — all different shapes, all still blocked or out of scope, per `run-lag.md`'s own
  per-mechanism table.

## How to verify

- A test asserting `ChangeCheckHistory` rows for `SourceKind = Cdc` get a non-null `SourceTimeUtc`, and
  `ChangeTracking` rows stay null.
- A test asserting the lag computation returns zero (not negative) when a mapping's own applied position
  is more recent than the group's latest history row.
- A test asserting a quiet source (no new writes, max LSN unchanged across ticks) does not show growing
  lag once a mapping is caught up — the specific bug the naive "now minus applied time" definition would
  have had.
- A test asserting the live-fetch fallback fires when no usable history row exists, and that it reuses
  `IChangeCounterSource`'s existing fail-open behavior rather than a new error path.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean if any API
  type changes reach the SPA.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-085-cdc-lag-calculation.md`.
