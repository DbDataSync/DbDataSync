# SQL Server CDC: source-side batching, and a guaranteed-delivery mode for SCD

**Status: draft, 2026-08-30 — two related follow-ups, recorded together since they touch the same
reader.**

## Follow-up 1: row-bounded reads for CDC

Deferred out of phase 56 (`chunked-apply-and-bounded-reads.md`), which scoped row-bounding to Watermark
and Change Tracking only. Named there as real, separate work because an LSN can cover a whole
transaction's worth of rows.

**Transactional integrity across a bounded read is explicitly not a goal.** DataSync doesn't promise to
apply a source transaction atomically as a unit today, and this follow-up shouldn't invent that guarantee
just because row-bounding makes a split more visible than an unbounded read did. What has to be preserved
is row-level ordering — LSN, then `__$seqval` within an LSN — not transaction grouping. The tie-safe
`TOP (@n) WITH TIES` / `FETCH FIRST @n ROWS WITH TIES` shape already settled for Watermark/CT is the
likely starting point, ordered by `(start_lsn, seqval)` instead of a single column, without needing to
special-case "don't split a transaction."

## Follow-up 2: a guaranteed-delivery CDC mode, for SCD correctness

**New, 2026-08-30.** A mode that guarantees every individual source change is tracked and applied — no
change silently coalesced or dropped — intended primarily for pairing with the SCD Type 2 writer (phase
55/`phase-055-snapshotting-and-scd-tracking.md`), which needs to witness *every* intermediate version of a
row, not just its net effect.

### What CDC already gives, and what it doesn't guarantee — confirmed against the shipped code, 2026-08-30

CDC already has the right foundation: unlike Change Tracking (which returns one net row per key at its
latest version, per `change-tracking-strategies.md`), `fn_cdc_get_all_changes_x` returns **every
individual DML operation** as its own row. Reading is not the gap.

**The gap is downstream, in the already-built SCD2 writer, and it's confirmed, not hypothetical.**
Read directly: `Scd2Writer.cs` / `HistorizedStatement.cs` (phase 55, done).

The writer applies a pass as **two set-based statements**, not row-by-row — its own doc comment says so
explicitly: *"Row by row would be correct and would take a pass proportional to the staged set rather
than to what actually changed."* Close: `UPDATE target ... WHERE IsCurrent = true AND EXISTS (a staged
row for this key that differs, or is a delete)`. Open: `INSERT ... SELECT ... FROM staging WHERE
not-a-delete AND NOT EXISTS (an open version for this key)`.

**If staging holds two rows for the same key in one pass — CDC's ordinary output for a row changed twice
between reads, not an edge case — both satisfy `NOT EXISTS (open version)` against the target's
pre-statement state, and the `INSERT...SELECT` tries to write both.** Worse: the surrogate key is
`prefix + naturalKey`, where `prefix` is **one timestamp for the whole pass**, not per row — so both
rows compute the *identical* surrogate key. Confirmed the surrogate key is the table's actual primary key
(`HistorizedProvisioning.cs`: `IsPrimaryKey: true`), so this collides on a PK violation, fails the
statement, rolls back the transaction (the writer's own `try`/`catch` does exactly that), and fails the
run. Confirmed no deduplication of staged rows by key happens anywhere upstream (staging, `RunExecutor`)
that would prevent this.

**Consequence**: a table with real per-row change frequency, paired with SCD2 over CDC, would hit this
deterministically — the same source data re-reads and re-stages the same duplicate-key batch on retry,
so the run fails *the same way every time* until intervention, not a transient hiccup. And even setting
the crash aside: the writer as built captures **one net version per key per pass**, not one version per
individual source change — the opposite of what "guaranteed to track every change" needs.

**Retention/position-loss is already handled separately**, and this mode inherits it rather than
reinventing it: `PositionExpiredException` (phase 32) already turns "the source discarded what we needed"
into a loud, distinct failure rather than silent data loss, for both CDC and CT. Not the gap here.

### What "guaranteed" requires, now that the mechanism is confirmed

- **Multiple staged rows for one key must become multiple, ordered versions** — not attempted by the
  set-based statements as built, and not safely retrofittable onto them (the whole point of the two-
  statement shape is operating on the *net* staged state per key). A guaranteed-delivery mode most likely
  needs the writer to fall back to **row-by-row processing, in source order, for any key with more than
  one staged row** — exactly the cost the current design deliberately avoided for the common case, paid
  only when a key actually has multiple pending changes.
- **Prerequisite fix, independent of "guaranteed" as a mode**: the PK-collision failure is a real defect
  in what's shipped, not a missing feature — worth its own fix (at minimum, detect same-key duplicates in
  staging and process them safely) regardless of whether the broader guaranteed-delivery mode gets built.
- **Whether "guaranteed" is a property of the reader, the writer, or the pairing** — likely declared the
  same way `DetectsDeletes` and phase 51's delete-blind-SCD2 warning already work: a capability an SCD2
  writer can require or warn about, not a silent assumption.

### Open questions

1. ~~Is the same-key collision a real, reproducible defect?~~ **Confirmed by code inspection — yes.**
   A reproduction test (two CDC changes to one key landing in one staged batch, run against `Scd2Writer`)
   would nail this down further, but the mechanism is clear enough from the statements themselves.
2. **Should the same-key-collision fix ship on its own, ahead of and separate from the broader
   guaranteed-delivery mode?** Leaning yes — it's a correctness bug in shipped code, not new scope.
3. Does guaranteeing full history require capping batches to at most one change per key upstream
   (simpler: never let the writer see the problem), or making the writer itself process same-key
   duplicates row-by-row in order within a batch (handles it wherever it originates, more moving parts)?
4. Exact mechanism for declaring/enforcing the guarantee — a reader+writer capability pair, or something
   else.

**Next step**: given the mechanism is now confirmed rather than suspected, the same-key PK-collision fix
(question 2) is probably worth splitting out as its own near-term item rather than waiting on the full
guaranteed-delivery design.

## Update, 2026-09-01 — Follow-up 1 resolved and scoped, on its own

An independent investigation (`change-queue-fairness-investigation.md`) arrived at this same gap from a
different direction — an uncapped CDC pass occupying a worker slot indefinitely — and phase 76's new
30-minute default command timeout raised the stakes further: an unbounded pass can now fail outright,
not just run long. `architecture/planning/done/cdc-row-bounded-reads.md` resolves Follow-up 1 on its own,
ahead of and independent of Follow-up 2 (the guaranteed-delivery mode and its PK-collision prerequisite
fix remain exactly as scoped above, untouched by this). Row-bounding is a real prerequisite for
guaranteed-delivery's row-by-row same-key handling, but this update ships it for the fairness/timeout
reason alone — Follow-up 2 doesn't need to be designed further before Follow-up 1 is built.
