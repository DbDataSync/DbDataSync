# Phase 51 — Data snapshotting and SCD Type 2 tracking (planned)

**Status**: Planned, not started
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
  not yet built), not here. Phase 51 only needs to make sure `IsCurrent`'s actual column name is
  discoverable — it's this phase's own provisioning naming decision (§3, an open question below) that
  phase 48 has to read.

### Resolved: current-only comparison, for phase 48 to build

**A verification check should have a "compare current only" option.** When set, the check's target-side
read is filtered to current rows only (`WHERE IsCurrent = 1`, or its equivalent), so a check against an
SCD2 target compares apples to apples — current source data against current target data — instead of the
source's row count against the target's full history.

Where the current-flag column name comes from: if the mapping's own writer is SCD2, the mapping already
knows its `IsCurrent` column name from its own provisioning config (§3) — the Checks editor should default
to that automatically rather than asking the operator to type the same column name twice. A check against
a Snapshot-written target has no equivalent "current" concept (every snapshot is a distinct, complete
copy, not a single row with a current flag) — "compare current only" for a snapshot target instead means
"compare against the most recent snapshot marker," a related but separate filter phase 48 should also
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
