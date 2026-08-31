# Chunking large change batches: staging is done, reading and applying aren't

**Status: draft, 2026-08-29.** Follow-up to a direct question about batching — see the survey below for
what's actually built today.

## The problem, as raised

Large, unchunked batches cause contention, unresponsiveness, and unexpected slowdowns during recovery.

## What's already true, and what isn't

Three stages, three different answers:

- **Reading is unbounded.** The "bounded window" pattern (`change-tracking-strategies.md`) bounds a
  read by *version range* (last watermark → current end position), read in one query via
  `IAsyncEnumerable`. Nothing bounds it by *row count* — a window with 2 million changes reads all
  2 million in one pass, whatever that costs the source.
- **Staging is genuinely chunked**, and has been since early on. `BatchInsertStagingProvider` batches
  multi-row `INSERT`s at a size computed from the target's parameter-count limit — not fixed, and
  `columnar-change-batches.md` measured the memory/throughput tradeoff of batch size directly.
- **Applying is a single statement, always.** `MsSqlMergeWriter` and `DeleteInsertWriter` each run one
  `MERGE` (or one set of `INSERT`/`UPDATE`/`DELETE`) against the *entire* staged set in one transaction,
  regardless of size. This is the one with no batching work behind it at all.

## Why this produces exactly the symptoms named

- **Contention / unresponsiveness**: one `MERGE` touching, say, 500k rows holds locks on the target table
  for the whole statement's duration — everything else hitting that table (a live read, another
  replication's writer, an operator's query) waits behind it.
- **Slowdowns during recovery**: phase 39 built real machinery for a killed worker's stranded claims and
  the failures it used to leave behind. But today's watermark model advances **only after the entire
  write succeeds** — so a run killed mid-apply, on a giant unchunked statement, has made *zero* durable
  progress. The next attempt redoes the whole apply from scratch. The bigger the unchunked batch, the
  more expensive every recovery attempt is, and the more likely a slow target makes it happen again before
  finishing.

## The central design fork: what does "chunking the apply" actually buy?

Two different answers, and they cost very differently to build:

- **(A) Chunk for lock/transaction size only.** Split the already-staged rows into sub-batches, apply
  each as its own statement/transaction. Reduces how long any single lock is held — real relief for
  contention and unresponsiveness. But the watermark still only advances once *all* chunks of a pass
  succeed, so a kill mid-apply still redoes the whole pass. Simple: no state-model change, just a loop
  around the existing writer call, applying sub-ranges of the staged set.
- **(B) Chunk for resumability too.** Persist progress *per chunk*, so a killed run resumes from the last
  committed chunk instead of redoing the whole apply. This is what actually fixes "unexpected slowdowns
  during recovery" as named, not just A's symptom-adjacent relief. It's a real change to the watermark
  model, though: today there is one watermark per mapping, advanced atomically after one write. Chunked
  resumability needs either sub-watermarks within a pass, or staged rows chunked in watermark/version
  order so each committed chunk corresponds to a real, resumable position — and it has to fit inside
  phase 39's single-writer state store and its existing recovery machinery rather than inventing a second
  one beside it.

**(A) is a writer-loop change. (B) is a state-model change.** They're not mutually exclusive — (A) is a
reasonable, small first step even if (B) is the actual goal — but they're different amounts of work and
worth deciding on explicitly rather than assuming the bigger one because it sounds more complete.

## The harder, deferred half: bounding the read side by row count

Even a chunked apply doesn't help if the *read* itself pulls an unbounded number of rows into the
pipeline before staging gets a chance to chunk anything downstream. Capping the read by row count instead
of version range is real per-engine work, and riskier than it looks:

- For SQL Server Change Tracking (and similarly CDC), `CHANGETABLE` returns one net row per key at its
  latest version — stopping at an arbitrary row count mid-version would need the new watermark to be a
  genuine, resumable version boundary, not "wherever we happened to stop." Splitting a version's rows
  across two passes needs to be proven safe (or avoided) per mechanism, not assumed safe generally.
  `task-run-errors-during-high-volume-workload.md`'s read-consistency race is a reminder of how sharp this
  area already is.
- This is realistically its own follow-on, engine by engine, once the apply-side answer (A vs. B) is
  settled — not something to solve in the same pass as the writer change.

## Resolved scope (2026-08-29)

- **Apply-side: (A) now, (B) as a named follow-on.** Chunk the apply step for transaction/lock size —
  a writer-loop change, no watermark-model change. Full crash-resumability (per-chunk durable progress)
  is real, wanted, and deliberately deferred to a later phase once (A)'s real-world impact is known and
  phase 39's recovery machinery can be extended deliberately rather than as a rider on this phase.
- **Read-side: in scope now, not deferred.** Bounding reads by row count goes in the same phase as (A),
  accepting the added per-engine correctness work that entails. The riskier mechanisms (SQL Server
  CDC, Postgres logical replication) need their own careful boundary-safety check per the reasoning
  above — the phase should do this for the reference implementations first (plain Watermark, SQL Server
  Change Tracking) and treat the rest as real, separate follow-on work rather than assuming the same
  technique ports safely without checking.

---

# Outcome

Agreed, as `implementation/todo/phase-056-chunked-apply-and-bounded-reads.md`. (Originally drafted as
055; renumbered to 056 after 055 turned out already taken by the snapshotting/SCD phase.)
