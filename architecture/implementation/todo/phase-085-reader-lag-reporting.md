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

### Change Tracking: time figure — exact via `dm_tran_commit_table`, falling back to an estimate

**Try the exact path first.** `sys.dm_tran_commit_table` maps a commit sequence number to its commit
time, and Change Tracking's `SYS_CHANGE_VERSION` values are commit sequence numbers:

```sql
SELECT tc.commit_time
FROM sys.dm_tran_commit_table AS tc
WHERE tc.commit_ts = @version;
```

This is exact, the same role `sys.fn_cdc_map_lsn_to_time` plays for CDC — but `dm_tran_commit_table` is
a DMV holding a bounded, rolling window of recent commits, not indefinite history. A version old enough
to have aged out returns no row: a real "too old to map" answer, not an error, and the mirror of CDC's
own `cdc.lsn_time_mapping` retention limit.

**Fall back to the estimate when the DMV has aged the version out — not otherwise.** A method returning
`TimeSpan?`, explicitly and separately labeled "estimated" wherever it surfaces (API field name, any
future UI text) — never merged with or presented identically to the exact DMV path or to CDC's exact
figure. Scan the mapping's group's `ChangeCheckHistory` rows for `SourceKind = ChangeTracking`, ascending
by `CheckedAtUtc`, for the first row whose `Value` (parsed as `long` in application code — not compared
as text in SQL, and not compared as text across engines; see the plan doc's note on why) is `>=` the
mapping's own applied version. That row's `CheckedAtUtc` is the estimate's anchor;
`estimatedLag = latestGroupCheckedAtUtc - anchorCheckedAtUtc`, same zero-clamp discipline as CDC. A
mapping with neither a DMV answer nor a usable history row returns null, not a synthesized zero or an
exception.

**The response shape must say which path answered.** A caller can't treat an exact `dm_tran_commit_table`
result and a `ChangeCheckHistory`-estimated one as interchangeable — pick a shape (a discriminated
field, separate nullable properties, whatever fits this codebase's existing API conventions) that makes
the distinction impossible to lose, not just documented.

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
- Change Tracking exact (version count): version-count arithmetic against known stored/current values.
- Change Tracking time, exact path: a version still within `dm_tran_commit_table`'s window resolves via
  the DMV join and is labeled exact, not estimated.
- Change Tracking time, fallback path: a version the DMV no longer holds falls back to the
  `ChangeCheckHistory` estimate; a built `ChangeCheckHistory` sequence crossing a mapping's applied
  version at a known tick, asserting the estimate lands on that exact tick's timestamp; a caught-up
  mapping reporting zero; a mapping with neither a DMV answer nor enough history reporting null, not
  zero or an exception; a small-numbers case (`Value` like `"9"` vs `"10"`) proving the parse-as-`long`
  comparison is actually used, not a text comparison that would order them wrong; and a test proving the
  exact and fallback paths are distinguishable in the response shape, not just in code.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
