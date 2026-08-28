# Run lag

**Status: proposal, not agreed — and the definition is the blocker, not the code.** Split out of
`run-metrics-and-monitoring.md`; the queryable half was resolved and became phase 36
(`planning/done/run-metrics.md`).

The phase 15 mockups show **median lag** on the Last-24-hours card and a **lag** column on the
replications list. Both were omitted, and unlike the rest of that card they cannot simply be queried:
nothing in the system records a notion of source-side event time versus apply time.

## Why no implementation phase was written

Because "lag" means at least two different things here and they have very different worth:

1. **Wall-clock since the last successful pass.** Trivial to compute — `TaskRuns` has the timestamps —
   and nearly useless: a replication that ran two minutes ago and found nothing looks identical to one
   that ran two minutes ago and is an hour behind.
2. **Watermark age** — how far behind the source's latest change the last successful pass got. This is
   the meaningful one, and it is only computable for a reader whose watermark is comparable to
   something the source can report *now*.

The second is the one worth building and it does not exist uniformly:

| reader | comparable "now"? |
| --- | --- |
| MsSqlChangeTracking | yes — `CHANGE_TRACKING_CURRENT_VERSION()` against the stored version, though the difference is a version count, not a time |
| MsSqlCdc (phase 32) | yes — `sys.fn_cdc_map_lsn_to_time` turns both LSNs into times, so this one is genuinely a duration |
| PgLogicalSlot (phase 34) | yes — slot lag in bytes, which is a size and not a time |
| Watermark | only if the watermark column is a timestamp, which is common but not required |
| BatchReload | **no** — there is no position at all |

So lag is not one number. It is per-mechanism, sometimes a duration, sometimes a count, sometimes bytes,
and sometimes undefined.

## What has changed since this was written

Phases 32 and 34 both add a reader whose position maps to a time or a size, and both already need to
*surface* that for their own reasons — CDC to detect a stopped capture job, Postgres to warn before a
replication slot fills the disk. That suggests the answer: **lag is a capability a reader declares**,
in the same opt-in-by-interface shape as `ISegmentExpandingReader` and `IConnectionTester`, returning a
value *and its unit*. A reader that has no answer says so, and the UI shows "not applicable" rather
than a dash that reads like zero.

That is a real design, but it should be written after phases 32 and 34 exist rather than in front of
them — both will have learned something about what their position actually compares against.

**Next step**: build 32 and 34, then come back with two working examples instead of a table of guesses.
