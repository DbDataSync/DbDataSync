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

**Estimated — a time, reconstructed from when we happened to observe each version.** Change Tracking has
no engine-side version-to-time mapping — nothing like `fn_cdc_map_lsn_to_time` exists for it. The only
source of a time estimate is the `ChangeCheckHistory` rows this system itself already wrote: scan a
mapping's group's history, ascending by `CheckedAtUtc`, for the *first* row whose `Value` is `>=` the
mapping's own applied version — that row's `CheckedAtUtc` is the best available estimate of when the
source reached (at least) that version, bounded above by however long it had already been true before
that particular poll happened to catch it. `estimatedLag = latestGroupCheckedAtUtc - crossingRowCheckedAtUtc`,
same zero-clamp discipline as CDC (a mapping caught up to the latest observed version has its own
crossing row *be* the latest row, giving zero).

This is explicitly an estimate, and must be labeled as one everywhere it's shown — its precision is
bounded by how often the gate happens to poll that group, not by anything about the underlying data.
**No live fallback is possible for this figure specifically** — unlike CDC, there's no source-side call
that answers "what time did version N first exist," only the polling history. A mapping in a
replication whose gate hasn't accumulated enough history yet (fresh install, or a group polled too
infrequently to have a row past the mapping's version) has no usable estimate and should say so — null,
not a synthesized zero.

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
- Change Tracking estimated: a test building a `ChangeCheckHistory` sequence crossing a mapping's applied
  version at a known tick, asserting the estimate lands on that tick's timestamp, not an earlier or
  later one. A test asserting a caught-up mapping (applied version equals the group's latest observed
  version) reports zero. A test asserting a mapping with insufficient history reports null, not zero or
  an exception.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-085-reader-lag-reporting.md`.
