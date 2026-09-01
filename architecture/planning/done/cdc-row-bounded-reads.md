# Row-bounded reads for CDC

**Status: resolved — ready for an implementation phase doc.**

## Where this came from

Two independent paths arrived at the same missing capability:

- `change-queue-fairness-investigation.md`: `MsSqlCdcReader` has no row-cap mechanism at all (confirmed
  by `grep`, zero references to `BoundedRead` anywhere in that file), so an uncapped CDC backlog reads,
  stages, and writes everything found in one uninterrupted `WorkItem`, occupying one of a replication's
  worker slots for however long that takes.
- `mssql-cdc-source-batching-and-guaranteed-delivery.md`'s "Follow-up 1": deferred out of phase 56
  (which scoped row-bounding to Watermark/Change Tracking only), needed because SCD2's guaranteed-
  delivery mode wants ordered, bounded batches to process same-key duplicates row-by-row rather than as
  one set-based statement.

Same feature, two justifications. Building it once serves both.

## What changed since both docs were written

Phase 76 shipped configurable connect/command timeouts, defaulting `CommandTimeoutSeconds` to **1800s
(30 minutes)** through the new `CreateTimedCommand()` chokepoint. An uncapped CDC read that used to just
run long now has a real chance of **failing outright** at the 30-minute mark unless an operator
explicitly sets `CommandTimeoutSeconds = 0` on that connection — which nothing prompts them to do. What
was a scheduling/fairness concern is now also a correctness risk: a large first-pass or post-outage
catch-up can hard-fail on its own. This raises the priority of row-bounding; it isn't purely a nice-to-
have anymore.

## Design

Mirror `MsSqlChangeTrackingReader`'s existing shape (`BoundedRead.Read(options)`,
`MsSqlChangeTrackingReader.cs:89`), but ordered correctly for CDC: `mssql-cdc-batching`'s own scoping is
right — `TOP (@n) WITH TIES` / `FETCH FIRST @n ROWS WITH TIES` ordered by `(start_lsn, seqval)` instead
of a single column, since an LSN can cover a whole transaction's worth of rows and `__$seqval` is what
orders within one. `ReadResult.WatermarkAfterRead`/`Bounded` (`ReadResult.cs:20-36`) already carries the
"a bounded read persists how far it actually got" behavior generically — `MsSqlCdcReader` just needs to
populate it, the same as `MsSqlChangeTrackingReader` already does.

**Default the cap on, for both mechanisms** — resolved, not left opt-in. `mssql-cdc-batching`'s Follow-up
1 didn't take a position on default-vs-opt-in; the fairness investigation's own "what would fix it"
section did: leaving it opt-in doesn't fix the risk for a mapping nobody thought to configure, which is
exactly the mapping most likely to have an unbounded backlog (nobody thought about it because nobody
had a problem yet). A default cap size is an implementation-phase decision (headroom vs. too many small
passes), not settled here.

**Composes with phase 75's polling gate without new work**, confirmed: a capped, multi-pass drain with no
new source writes between passes is exactly the scenario phase 75's gate already handles correctly —
compares each mapping's own watermark against a freshly-fetched value, never a cached "last checked"
flag. The two were already designed to compose; this phase doesn't touch `ChangePollingGate` at all.

## What this phase should not do

- The guaranteed-delivery mode itself (`mssql-cdc-batching`'s Follow-up 2) — row-bounding is a
  prerequisite for it, not the mode. The same-key PK-collision bug in `Scd2Writer` that Follow-up 2
  documents is a separate, already-confirmed defect in shipped code; fixing it is independent of this
  phase and shouldn't be bundled into it.
- Transactional-integrity-across-a-split guarantees — explicitly not a goal, per `mssql-cdc-batching`'s
  own framing. Row-level ordering is preserved; transaction grouping is not.
- Row-bounding for any reader other than CDC — Change Tracking already has it.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-084-cdc-row-bounded-reads.md`.
