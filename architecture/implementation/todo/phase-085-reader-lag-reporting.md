# Phase 85 — Reader lag: CDC's real duration, and Change Tracking's two figures

**Status**: Not started.
**Plan reference**: `architecture/planning/done/reader-lag-reporting.md`, which builds the general
"reader declares a lag capability" design `architecture/planning/todo/run-lag.md` sketches, scoped to
CDC and Change Tracking now (Watermark mode is a separate fast-follow, structurally different — see the
plan doc's "Scope, resolved").

## The gap

Nothing computes a staleness figure for any replication today. `run-lag.md` names CDC and Change
Tracking as two of the mechanisms with *some* comparable "now," and this phase builds both — CDC as a
real duration via the engine's own LSN-to-time mapping, Change Tracking as an exact version count plus a
separately-labeled *estimated* duration reconstructed from this system's own polling history, since
Change Tracking has no engine-side equivalent to `fn_cdc_map_lsn_to_time`.

The naive definition — "now minus our last applied change's time" — is wrong for both: it grows forever
on a quiet, fully-caught-up source. Every figure below compares against the source's own current
position instead, never wall-clock now.

## What to build

### Schema: `ChangeCheckHistory.SourceTimeUtc`

Nullable column (`Migrations.cs`). Populated only for `SourceKind = Cdc`, via
`sys.fn_cdc_map_lsn_to_time(maxLsn)` in `DriverChangeCounterSource.FetchAsync`
(`ChangeCounterSource.cs`), on the same round-trip that already fetches the max LSN and has already
switched to the right database. Null for `ChangeTracking` rows — a version count has no time mapping.
`ChangePollingGate.RecordCheck` (`ChangePollingGate.cs:151`) gains one more column to write.

### CDC lag

A method returning `TimeSpan?` for a mapping: look up its current `ChangeWatermarks` position (phase
74, keyed by `MappingName`; null → return null, first pass has nothing to compare), map it to a time via
`sys.fn_cdc_map_lsn_to_time` (on demand, mapping-specific), compare against the latest
`ChangeCheckHistory.SourceTimeUtc` for that mapping's `(ConnectionName, SourceDatabase, Cdc)` group
(falling back to a live fetch through `IChangeCounterSource`, extended to also return the mapped source
time, when no usable history row exists), clamp negative results to zero.

### Change Tracking: exact figure

A method returning a version-count `long?` (or similar — your call on the exact numeric type,
consistent with how `ChangeWatermarks`/`ChangeCheckHistory` already store versions): the group's latest
`ChangeCheckHistory.Value` for `SourceKind = ChangeTracking` minus the mapping's own stored
`ChangeWatermarks` version. Both values already exist; no new fetch, no estimation.

### Change Tracking: estimated figure

A method returning `TimeSpan?`, explicitly and separately labeled "estimated" wherever it surfaces
(API field name, any future UI text) — never merged with or presented identically to CDC's exact figure.
Scan the mapping's group's `ChangeCheckHistory` rows for `SourceKind = ChangeTracking`, ascending by
`CheckedAtUtc`, for the first row whose `Value` (parsed as `long` in application code — not compared as
text in SQL, and not compared as text across engines; see the plan doc's note on why) is `>=` the
mapping's own applied version. That row's `CheckedAtUtc` is the estimate's anchor;
`estimatedLag = latestGroupCheckedAtUtc - anchorCheckedAtUtc`, same zero-clamp discipline as CDC. No live
fallback exists for this figure — there's no source-side call that answers "what time did version N
first exist." A mapping whose group has no history row past its own applied version returns null, not a
synthesized zero or an exception.

### API

Expose all three figures (CDC's `TimeSpan?`, Change Tracking's exact version-count and estimated
`TimeSpan?`) so a future notifications latency trigger or UI figure can consume them. Match this
codebase's existing per-mapping-status API shape rather than inventing a new pattern — check what's
already returned alongside a mapping's run history before adding a parallel endpoint.

## What this phase should not do

- Watermark mode's lag (timestamp-column-based) or BatchReload's "not applicable" declaration — same
  interface, later phase.
- The notifications latency trigger, or a UI card/column — separate work, not required to prove these
  computations.
- Postgres (phase 34) — unaffected.

## How to verify

- CDC: non-null `SourceTimeUtc` only for `Cdc` history rows; zero-not-negative clamping; no false growth
  on a quiet caught-up source (the specific bug the naive definition would have); live-fetch fallback
  when no usable history exists.
- Change Tracking exact: version-count arithmetic against known stored/current values.
- Change Tracking estimated: a built `ChangeCheckHistory` sequence crossing a mapping's applied version
  at a known tick, asserting the estimate lands on that exact tick's timestamp; a caught-up mapping
  reporting zero; a mapping with insufficient history reporting null, not zero or an exception; and a
  small-numbers case (`Value` like `"9"` vs `"10"`) proving the parse-as-`long` comparison is actually
  used, not a text comparison that would order them wrong.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
