# Phase 55 — Data snapshotting and SCD Type 2 tracking

**Status**: Done
**Plan reference**: `architecture/planning/done/data-snapshotting-and-scd-tracking.md`

## What this covers

Two new `IChangeWriter` implementations — a snapshot writer and an SCD Type 2 writer — plus the
provisioning support both need for bookkeeping columns beyond the mapped ones, and the binding-UI
labeling SCD2 needs when paired with a delete-blind reader.

## 1. Snapshot writer

New **generic** writer (`DataSync.Drivers.Generic`, dialect-parameterized like `DeleteInsertWriter`, not
engine-specific — nothing here needs a driver-specific bulk trick):

- Inserts every row in the staged change set, unconditionally — no update, no delete, no comparison
  against what's already there. Every row carries a snapshot marker column (a timestamp or run id,
  written by the writer, not mapped from the source).
- `SupportsReconciliation` is `false` — there's no notion of removing what's absent; every snapshot is
  purely additive.
- Typically paired with `BatchReload` (a full read) on a schedule, not a watermark/log reader — a
  snapshot is "everything, right now." Nothing prevents pairing it with another reader, but the binding
  UI should suggest `BatchReload` as the natural fit.
- **No dedup logic** — per the resolved design decision, every snapshot writes every row, always. Simplest
  possible writer.

## 2. SCD Type 2 writer

New **generic** writer, same placement rationale as the snapshot writer:

- For each incoming row (keyed by the mapping's business/natural key): if the key has no open (current)
  version in the target, insert one. If it has an open version and the mapped values differ, close the
  open version (`ValidTo = now`, or `IsCurrent = false`) and insert a new one (`ValidFrom = now`,
  `IsCurrent = true`). If the mapped values are unchanged, do nothing.
- **Surrogate key is DataSync-computed** — a deterministic value (e.g. a hash of the natural key plus
  `ValidFrom`), computed before insert. No write-back from the target is needed; `ApplyAsync`'s existing
  signature is unchanged.
- **Deletes**: when the reader detects deletes (`ChangeRow` carrying a delete operation), the
  corresponding open version is closed with no replacement. When the reader does **not** detect deletes,
  the writer has no way to know a key disappeared — per the resolved decision, this is *allowed*, not
  rejected, but the row will remain "current" forever. See §4 for where this gets surfaced.
- `SupportsReconciliation` is `false` for the same reason as the snapshot writer — a full "make the target
  match" pass doesn't apply to a writer whose entire job is preserving what used to be there.

## 3. Provisioning: bookkeeping columns

`IProvisioner`/`CreateTableStatement` need to add columns beyond the mapped ones when the configured
writer is Snapshot or SCD2:

- **Snapshot**: one snapshot marker column (name/type TBD during implementation — a timestamp is the
  obvious default).
- **SCD2**: the surrogate key column, `ValidFrom`, and `ValidTo`/`IsCurrent`. The surrogate key becomes
  the table's real primary key in generated DDL — the source's own key is no longer unique in this
  target.

This extends the same machinery phase 45 §8 already planned (`alterTargetTableColumnsIfMissingOrChanged`)
rather than inventing a second provisioning path — these bookkeeping columns are exactly the kind of
"columns the mapping needs that aren't one-to-one with the source" that work already has to account for.

## 4. Binding UI: SCD2 + a delete-blind reader must say so

Wherever a writer is chosen for a mapping (the pipeline/writer picker), selecting SCD2 while the
configured reader's `DetectsDeletes` is `false` must show a clear, visible warning: deleted source rows
will remain "current" in this target forever. Not a validation error — an informed, explicit choice.

## 5. Same connection, remote target

No new code path for "source and target are the same connection" — the writer operates against
`targetConnection` exactly as every writer does today. The one real requirement: **the target table must
be a distinct table object from the source**, even when they share a connection. Add a check (validation
at save, matching phase 16's "reject at config time, not at run time" precedent) that a mapping using
Snapshot or SCD2 cannot have an identical source and target `TableRef` — the general "same connection"
case otherwise needs nothing extra.

## What this phase does not build

- **"As of" querying** — no view, no helper. The target carries `ValidFrom`/`ValidTo`/`IsCurrent` (or a
  snapshot marker); querying it is left to the operator, explicitly, per the planning doc.
- **Retention/purging** of old snapshots or closed SCD versions — deliberately out of scope, same
  precedent as `run-metrics.md` and phase 43: a policy question, not an implementation one.
- **Building the verification-side fix itself** — resolved in direction (see below), but the actual
  `VerificationCheckConfig` field and the check-execution filter belong to phase 48 (the Checks editor,
  not yet built), not here. Phase 55 only needs to make sure `IsCurrent`'s actual column name is
  discoverable — it's this phase's own provisioning naming decision (§3, an open question below) that
  phase 48 has to read.

### Resolved: current-only comparison, for phase 54 to build

**A verification check should have a "compare current only" option.** When set, the check's target-side
read is filtered to current rows only (`WHERE IsCurrent = 1`, or its equivalent), so a check against an
SCD2 target compares apples to apples — current source data against current target data — instead of the
source's row count against the target's full history.

Where the current-flag column name comes from: if the mapping's own writer is SCD2, the mapping already
knows its `IsCurrent` column name from its own provisioning config (§3) — the Checks editor should default
to that automatically rather than asking the operator to type the same column name twice. A check against
a Snapshot-written target has no equivalent "current" concept (every snapshot is a distinct, complete
copy, not a single row with a current flag) — "compare current only" for a snapshot target instead means
"compare against the most recent snapshot marker," a related but separate filter phase 54 should also
account for.
- Engine-specific optimized versions of either writer (a bulk-MERGE-based SCD2, say) — the generic
  version is the first cut, same phasing precedent as `DeleteInsertWriter` before `MsSqlMergeWriter`.

## How to verify when built

- A snapshot writer run twice against an unchanged source produces two full copies, not one — confirms no
  dedup logic exists.
- An SCD2 writer: a new key inserts one open version; a changed key closes the old version and opens a
  new one with correct `ValidFrom`/`ValidTo`; an unchanged key produces no write at all.
- An SCD2 writer paired with a delete-detecting reader closes a version when its source row is deleted.
- An SCD2 writer paired with a delete-blind reader leaves a version open when its source row is deleted
  externally (confirms the accepted limitation behaves as documented, not silently wrong).
- The writer picker shows the delete-blind warning when SCD2 is selected with a non-deleting reader, and
  shows nothing when paired with one that detects deletes.
- Saving a mapping with Snapshot or SCD2 configured and an identical source/target `TableRef` is rejected
  at save.
- Provisioning generates the correct bookkeeping columns for both writer kinds, and the SCD2 surrogate key
  is the generated table's primary key.
- Full suite green.

## Open questions

- Exact column names/types for the bookkeeping columns (`ValidFrom`/`ValidTo` vs. `EffectiveFrom`/
  `EffectiveTo`, etc.) — naming detail for implementation.
- Whether the SCD2 "values differ" comparison is column-by-column equality or something coarser (a row
  hash, mirroring the snapshot-diff strategy already named in `change-tracking-strategies.md`) — affects
  performance on wide tables, not correctness.

---

# Retrospective

Two generic writers, both engine-neutral, both verified against a real database — because what they do
is decided by SQL and every interesting case is about what happens on the *second* pass.

## The natural key cannot be inferred, and finding that out cost a rewrite

`Scd2Writer` first derived the business key from the target's primary key, excluding the surrogate.
That is circular and it failed on the first run against a real table: in an SCD2 target the surrogate
**is** the primary key, and the natural key is not a key at all — which is exactly what this phase's own
provisioning creates.

It is a declared setting now (`naturalKey`), required, offered by the parameter system like every other
writer option. That is also the more honest answer: which columns make a customer the same customer is
a question about the business, not about the schema.

## Two dialect divergences, both found by running it

Neither would have surfaced from reading the code, and both make a statement work on exactly one
engine:

- **A boolean literal.** A canonical `Boolean` renders as `boolean` on Postgres and `bit` on SQL
  Server, and `= 1` against the former is *"operator does not exist: boolean = integer"*.
- **String concatenation.** `||` everywhere, `+` on SQL Server.

`SqlDialect` gained `TrueLiteral`, `FalseLiteral` and `Concat`. Small additions, and the kind that only
appear when the generic layer is actually exercised on two engines.

## Null-safety is the silent one

`t.c <> s.c` is *unknown* when either side is null, so a value becoming null — or arriving where there
was none — would not register as a change and the version would never close. The target then reports
stale data as current, with nothing wrong on the surface.

Each column is compared with an explicit null-handling pair instead. Asserted twice: in the SQL, and
against a real database in both directions.

A row hash would be cheaper on a wide table and is the obvious next step. It is not the first cut,
because a hash makes "which column changed" unanswerable while this is still being trusted.

## The snapshot writer's whole design is a refusal

No comparison, no dedup, no delete. A writer that skipped unchanged rows would produce copies that are
not snapshots: the row missing from Tuesday's would mean "unchanged" to somebody who knows the
implementation and "deleted" to everybody else. Asserted by running it twice against an unchanged source
and finding two complete copies under two distinct markers — the second of which is what phase 54 reads.

## Provisioning extends the list it already had

The bookkeeping columns go into the same `ProvisioningColumn` list `CreateTargetTable` and
`AlterTargetTable` already work from, which is what phase 45 §8 was built to allow. The SCD2 surrogate
becomes the table's primary key and the mapped columns stop being one — a target that keeps every
version of a key has that key many times over, so leaving it as the primary key would make the *second*
version of anything a constraint violation on the first write.

## Same source and target is refused at save

A historizing writer pointed at its own source grows the table on every pass, and the next pass reads
what the last one wrote. Refused when a mapping is saved, following phase 16's precedent — finding out
at run time means finding out after it has already doubled the table. Same *connection* is fine and
needs nothing; it is the same table object that cannot work.

## Verification

- `HistorizedStatementTests` (11) — the snapshot's absence of a predicate, null-safe comparison in both
  directions, a delete closing a version, only the open version closing, a key-only table where nothing
  can change, versions opened only where none is open, no replacement for a delete, the surrogate
  computed from the natural key, a composite key, and the dialect supplying booleans and concatenation.
- `HistorizedWriterTests` (8, integration, Postgres) — two passes producing two complete snapshots under
  two markers; a new key opening one version; a changed key closing and opening; an unchanged key
  writing nothing at all; a value becoming null and a value arriving closing the version; a delete
  closing with no replacement; and **a delete-blind reader leaving a disappeared key current**, which is
  the accepted limitation behaving as documented rather than silently wrong.
- `HistorizedTargetValidationTests` (6) — both historizing writers refused against their own source, the
  same connection and the same name in another schema allowed, and ordinary writers unaffected.
- Playwright 38 — the delete-blind warning appearing for SCD2 with a watermark reader, absent with a
  delete-detecting one, and gone again with a writer that keeps no history.

## Open questions

- ~~**Bookkeeping column names.**~~ `DS_SnapshotAt`, `DS_VersionKey`, `DS_ValidFrom`, `DS_ValidTo`,
  `DS_IsCurrent`, named once in `HistorizedColumns` so phase 54 and provisioning read the same
  constant rather than three agreeing strings.
- ~~**Column-by-column or a row hash.**~~ Column by column, for the reason above.
- **New**: `DS_ValidTo IS NULL` and `DS_IsCurrent` say the same thing, written together in one
  statement. The redundancy is deliberate — an indexable flag is what makes "the current row" cheap —
  but nothing enforces that they agree, and a hand-written UPDATE against the target could separate
  them.
- **New**: the surrogate key is the pass timestamp plus the natural key, cast to text and concatenated.
  Unique and legible, and longer than a hash would be. `VARCHAR(200)` will not hold a wide composite
  key, and nothing checks.
