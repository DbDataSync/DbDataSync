# Investigation: does a large-backlog table starve other tables in the change queue?

**Resolved 2026-09-13 — became `architecture/implementation/done/phase-084-cdc-row-bounded-reads.md`.**
That phase's own header names this doc directly: "the fix `change-queue-fairness-investigation.md`
proposed independently." It built exactly what "What would fix it, if built" (below) describes —
`MsSqlCdcReader` gained the same `BoundedRead` mechanism `MsSqlChangeTrackingReader` already had, the
cap defaults **on** (`BoundedRead.DefaultMaxRows = 50_000`) for both instead of staying opt-in, and a
capped read persists its position and re-enqueues for the remainder, so a large backlog now becomes
many bounded `WorkItem`s that interleave with other due mappings through the existing `TryClaimNext`
ordering rather than occupying one worker slot indefinitely. Phase 84 also verified this composes
correctly with phase 75's polling gate (the interaction flagged below) with a dedicated test, rather
than assuming it.

Separately, `ProcessSupervisor.BuildStartInfo` (`src/DbDataSync.Api/Services/ProcessSupervisor.cs:127`)
now passes a real, per-replication `--degree-of-parallelism` — sourced from the replication's own
`ChangeProcessingConfig.DegreeOfParallelism`, falling back to the default only when unset — rather than
the API always spawning workers at the previously-unconfigurable default of 4 this doc's "What this is,
precisely" section describes.

The rest of this document is kept as the original investigation record.

## What was checked

`RunExecutor.ExecuteWorkerAsync` (`RunExecutor.cs:70`): one OS process per *replication* (task), so
different replications are already fully isolated — a huge backlog on one replication's table cannot
starve a different replication's tables at all. The fairness question only exists *within* one
replication, across its own table mappings, sharing that process's `degreeOfParallelism` workers.

Within one replication:

- `WorkQueueStore.TryClaimNext` (`WorkQueueStore.cs:154-202`) orders candidates
  `Priority DESC, EnqueuedAtUtc ASC` and excludes any mapping that already has a `Claimed`/`Running`
  item (`NOT EXISTS` self-join, line 166-169). **This already prevents one mapping from occupying more
  than one worker slot at a time** — a real, existing fairness mechanism, not something missing.
- The gap is size, not count: an ordinary incremental pass is **one `WorkItem`**, and nothing bounds how
  much work that one item can contain. `MsSqlChangeTrackingReader` supports an opt-in row cap
  (`BoundedRead.Read(options)`, `MsSqlChangeTrackingReader.cs:89`) — off unless a mapping's config turns
  it on. `MsSqlCdcReader` has **no row-cap mechanism at all** — confirmed by `grep`, zero references to
  `BoundedRead` anywhere in `MsSqlCdcReader.cs`. A CDC-tracked table with millions of pending changes and
  no cap reads, stages, and writes all of them in one uninterrupted `WorkItem`, occupying one of
  `degreeOfParallelism` worker slots for however long that takes — minutes to hours, depending on volume.
- During that window, every other due mapping in the same replication is still schedulable (the exclusion
  above only blocks the *busy* mapping, not others), but they're competing for the remaining
  `degreeOfParallelism - 1` slots. With a low DOP (the API-spawned default is 4, per
  `ProcessSupervisor.BuildStartInfo` never passing `--degree-of-parallelism` — see the earlier
  conversation about phase 74/75's neighborhood) and more than a few large tables active at once, this
  degrades from "reduced headroom" toward genuine, long delays for everything else in that replication.

## What this is, precisely

Not starvation in the strict sense — no mapping is ever permanently blocked, and the exclusivity rule
already stops one mapping from hoarding multiple slots. It is **unbounded worst-case occupancy**: one
`WorkItem`'s size is whatever the source produced since the last watermark, with no ceiling, so one
table's catch-up (after a long outage, or its first pass after enabling CDC) can dominate most of a
replication's parallelism for an extended, unpredictable duration.

## What would fix it, if built

Extend `BoundedRead`-style row-capping to `MsSqlCdcReader` (new capability — doesn't exist today), and
default a cap on for both mechanisms rather than leaving it opt-in. A capped pass advances only partway
and re-enqueues itself for the remainder (this is exactly `ReadResult.WatermarkAfterRead`'s existing
"a bounded read persists how far it actually got" behavior, already built for phase 73/74/75's neighboring
work — see `ReadResult.cs:20-36`). A large backlog then becomes many bounded `WorkItem`s instead of one
unbounded one, and because each capped chunk re-enters the same `TryClaimNext` ordering
(`EnqueuedAtUtc ASC`), other due mappings' items — enqueued in between — get interleaved naturally rather
than waiting behind one giant pass.

This has a real interaction with phase 75's polling gate worth flagging for whoever picks this up: a
capped, multi-pass drain with no new source writes between passes is exactly the scenario phase 75's gate
was corrected to handle (compare each mapping's own watermark against a freshly-fetched value, not a
cached "last checked" flag) — the two are already designed to compose correctly, not fought over
separately.

**Deliberately not designed further here** — this doc is the investigation the user asked for, not a
committed plan. If this is picked up later, it needs its own default-cap-size decision (how many rows is
"enough headroom but not too many small passes") and probably belongs in the same conversation as
`architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md`, which is already
sitting in the backlog and touches the same reader.

**Next step**: none — resolved by phase 84, above.
