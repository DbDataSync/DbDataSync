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

## What "lag" should mean (2026-08-28)

Both numbers from the phase 15 mockups are wanted, not one instead of the other — they answer different
questions and both belong on the card:

1. **Time since last completed pass** — wall-clock since the most recent successful run finished. This
   is the trivial one (item 1 above); it stays in scope alongside the harder number rather than being
   dropped as "nearly useless" on its own. It answers "is this replication still running at all."
2. **Time difference between source and target applied data** — the watermark-age number (item 2
   above), expressed as a *time* wherever the reader's position can be converted to one. This is the
   one that answers "how stale is the data."

This confirms the per-mechanism table still matters for (2): a reader that can't produce a time-based
answer (PgLogicalSlot's bytes, MsSqlChangeTracking's version count, BatchReload's nothing) should say so
rather than force a number, per the "declares a capability, with its unit, or says not applicable"
design above. (1) is uniform across all readers and doesn't need the capability interface at all.

**Next step**: build 32 and 34, then come back with two working examples for (2) instead of a table of
guesses. (1) has no such blocker and could be built any time — it's just less valuable alone.
