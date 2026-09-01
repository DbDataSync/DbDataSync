# Reader lag: a general capability, built now with CDC and Change Tracking

**Status: resolved — ready for an implementation phase doc. Revised from an earlier CDC-only draft
before implementation started.**

## Where this came from, and the correction

`run-lag.md`'s "watermark age" half has always been blocked on needing a reader whose position is
comparable to something the source can report *now*. The first draft of this doc scoped only CDC,
reasoning that the general "reader declares a lag capability, with its unit" design should wait for a
second real example (Postgres, phase 34) before being built, per `run-lag.md`'s own caution against
guessing at an interface from one data point.

That reasoning was wrong on its own terms: `run-lag.md`'s per-mechanism table already lists two *other*
implementable examples that don't depend on Postgres at all — Change Tracking (a version-count
difference, not a time, but a real "how far behind" number) and Watermark mode (a real time, but only
when the configured column happens to be a timestamp). Three examples exist today; Postgres becomes a
fourth implementor later, not the blocking second one. The general capability is worth building now.

## Scope, resolved

**CDC and Change Tracking, both in this phase.** Watermark mode is structurally different — its
position is per-table, not database-wide, so it can't reuse `ChangeCheckHistory`'s shared-fetch pattern
the way CDC and Change Tracking both do; it needs its own live per-table query. Left for a fast-follow
phase using the same interface, not built here. BatchReload declares no capability at all — "not
applicable," never a dash that reads like zero.

## The capability, in shape

Matches `run-lag.md`'s own sketch: a reader-side capability, in the same opt-in-by-interface shape as
`ISegmentExpandingReader`/`IConnectionTester`, returning a value *and* its unit — a real `TimeSpan` for
CDC, a version count for Change Tracking's exact figure, and (this phase adds) an *estimated* `TimeSpan`
for Change Tracking too, distinct from and clearly labeled apart from the exact one. A reader with
nothing to report says so explicitly rather than the UI inferring "not applicable" from an absence.

## CDC: real duration, via the engine's own mapping

Unchanged from the first draft. `sys.fn_cdc_map_lsn_to_time` turns an LSN into the wall-clock time its
transaction committed — a genuine duration, not an approximation. Add a nullable `SourceTimeUtc` column
to `ChangeCheckHistory` (phase 75), populated only for `SourceKind = Cdc`, computed in
`DriverChangeCounterSource.FetchAsync` (`ChangeCounterSource.cs`) on the same round-trip that already
fetches the max LSN — the connection is already open, already switched to the right database. Per-
mapping lag = (latest group `SourceTimeUtc`) minus (the mapped time of the mapping's own current
`ChangeWatermarks` position, computed on demand — mapping-specific, not cacheable in the shared table).
Clamp negative results to zero. Falls back to a live fetch (extending `IChangeCounterSource`) when no
usable history row exists.

**The naive definition is still wrong, for the same reason**: comparing against wall-clock now instead
of the source's own current position makes lag grow forever on a quiet, fully-caught-up source. Both
figures below inherit this same discipline.

## Change Tracking: two figures, not one

**Exact — a version count.** `CHANGE_TRACKING_CURRENT_VERSION()` (already the `Value` `ChangeCheckHistory`
stores for `SourceKind = ChangeTracking`) minus the mapping's own stored version
(`ChangeWatermarks`). Always live-computable from data already fetched by the existing gate — no new
round-trip, no estimation, exact by construction. This is the number an operator who understands their
own change volume can reason about directly ("40 versions behind" on a table that bumps a few times a
second means something different than on one that bumps daily).

**Time — updated 2026-09-01: a real engine-side mapping exists after all, with a fallback for when it
doesn't reach far enough back.**

`sys.dm_tran_commit_table` maps a commit sequence number to the wall-clock time it committed, and Change
Tracking's `SYS_CHANGE_VERSION` values *are* commit sequence numbers — joining
`dm_tran_commit_table.commit_ts = SYS_CHANGE_VERSION` gives a real, exact commit time for a given
version, the same role `sys.fn_cdc_map_lsn_to_time` plays for CDC:

```sql
SELECT tc.commit_time
FROM sys.dm_tran_commit_table AS tc
WHERE tc.commit_ts = @version;
```

**The caveat this doc should state even though the discovery didn't**: `sys.dm_tran_commit_table` is a
dynamic management view, not a persisted table — it holds a bounded, rolling window of recent commits,
not indefinite history. A version old enough to have aged out of that window returns no row, which is a
real, expected answer ("too old to map"), not an error. This is the mirror of CDC's own retention
caveat (an LSN outside `cdc.lsn_time_mapping`'s retained window also maps to nothing) — same shape,
different mechanism underneath.

So: **try the DMV join first.** It succeeds for any version recent enough to still be in the window,
which is the common case for a mapping that's even roughly keeping up. **Fall back to the previously-
designed estimate** — scan the mapping's group's `ChangeCheckHistory` rows, ascending by `CheckedAtUtc`,
for the *first* row whose `Value` is `>=` the mapping's own applied version; that row's `CheckedAtUtc` is
the best available estimate of when the source reached (at least) that version, bounded above by however
long it had already been true before that poll happened to catch it —
`estimatedLag = latestGroupCheckedAtUtc - crossingRowCheckedAtUtc`, same zero-clamp discipline as CDC —
**only when the DMV has aged the version out.** The fallback is still real and still needed; it's no
longer the only path, and its own doc/comment/field naming should be honest that it's the fallback for
when the exact answer isn't available, not the primary mechanism.

**The API/response shape needs to say which path answered** — a caller (and eventually a UI) must be
able to tell an exact DMV-sourced time from an estimated poll-reconstructed one; these are not
interchangeable precision-wise and must never be presented identically. Exact naming/shape is an
implementation call, but the distinction itself is not optional.

A mapping whose group has *neither* a DMV answer *nor* enough polling history (fresh install, or a group
polled too infrequently to have a row past the mapping's version) has no usable time figure and should
say so — null, not a synthesized zero.

Parse `ChangeCheckHistory.Value` as `long` for the crossing-row scan; do this in application code rather
than as a numeric comparison in stored SQL — `Value` is stored as text, and a raw text comparison across
this store's three supported engines (SQLite/Postgres/SQL Server, phase 63) would order `9` above `10`,
the exact pitfall `ChangeCounters.Compare`'s own doc comment already calls out for this same table.

## What this phase should not do

- Watermark mode's timestamp-column lag, or BatchReload's "not applicable" declaration — real, and
  should use the same interface, but a fast-follow phase, not this one.
- The notifications latency trigger itself — a separate, not-yet-built phase; this one only builds and
  exposes the computation.
- A UI card/column — cheap once the numbers exist and are exposed via API, not required to prove either
  calculation works.
- Postgres (phase 34) — unaffected either way; this phase's existence doesn't pull that work forward.

## How to verify

- CDC: as the first draft specified — non-null `SourceTimeUtc` only for `Cdc` rows, zero-not-negative
  clamping, no false growth on a quiet caught-up source, live-fetch fallback on missing history.
- Change Tracking exact: a version-count test against known stored/current values, no estimation
  involved.
- Change Tracking time — exact path: a test asserting a version still within `dm_tran_commit_table`'s
  window resolves via the DMV join, not the fallback, and is labeled exact in the response shape.
- Change Tracking time — fallback path: a test asserting a version the DMV no longer holds falls back to
  the `ChangeCheckHistory` estimate; a test building a `ChangeCheckHistory` sequence crossing a mapping's
  applied version at a known tick, asserting the estimate lands on that tick's timestamp, not an earlier
  or later one; a test asserting a caught-up mapping reports zero; a test asserting a mapping with
  neither a DMV answer nor enough polling history reports null, not zero or an exception — and that the
  fallback result is labeled estimated, distinguishably from the exact path above.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-085-reader-lag-reporting.md`.
