# Phase 54 — "Current only" comparison for verification checks against historized targets (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/data-snapshotting-and-scd-tracking.md`, following up on
the gap that doc flagged and phase 55 (SCD2/Snapshot writers) carries forward.
**Depends on**: phase 55, which decides the bookkeeping column names this reads.

## The gap

A verification check (phase 43, execution; phase 48, editor — both done) compares a mapping's source and
target as if they're the same shape. An SCD2 or Snapshot target isn't: it legitimately carries more rows
than the source by design (history), so a row count check (or any check) against one today will report a
difference that isn't a defect — it's the feature working.

## The fix: a "compare current only" option

- **`VerificationCheckConfig`** gains a field (e.g. `compareCurrentOnly: bool` plus a
  `currentColumn: string | null` naming which column marks a row current) — when set, the check's
  target-side read filters to current rows only (`WHERE {currentColumn} = 1`, or the SCD2 writer's actual
  equivalent from phase 55 §3) before comparing against the source.
- **Default the column name from the mapping's own writer**, when it's SCD2: phase 55 already has the
  mapping declare its `IsCurrent` column name as part of its own provisioning config. The Checks editor
  (`VerificationPanel.tsx`'s Checks card, phase 48) should read that and pre-fill `currentColumn`
  automatically rather than asking the operator to type the same name twice — surface it as an editable
  default, not a hidden inference, since a check should still say plainly what it's filtering on.
- **A Snapshot-written target has no per-row current flag** — every snapshot is a complete, separate copy,
  not a single row toggling current/not-current. "Compare current only" against a Snapshot target instead
  means "compare against the most recent snapshot" (filter to the latest snapshot marker value), a related
  but distinct filter. The option's label/behavior needs to read correctly for both writer kinds rather
  than assuming SCD2's shape universally.
- **Execution-side change** (`DataSync.Verification`, the engine phase 43 built): the target-side query
  gains the filter when the option is set. Source-side reads are unaffected — the source has no history to
  filter, by definition.

## What this phase does not build

- Any change to how SCD2/Snapshot writers themselves work (phase 55).
- A general "as of" comparison (comparing source-as-of-some-past-date against a target's historical
  version) — this is specifically "current vs. current," not point-in-time comparison.

## How to verify when built

- A `RowCount` check against an SCD2 target with `compareCurrentOnly` set reports a matching count when
  source and current target rows agree, even though the target's full history table has more rows.
- The same check with the option unset reports the (expected, uninteresting) mismatch against full
  history — confirms the option is additive, not a silent behavior change for existing checks.
- Adding a check against a mapping whose writer is SCD2 pre-fills `currentColumn` from the writer's own
  configured column name.
- A check against a Snapshot target's "compare current only" filters to the latest snapshot marker rather
  than attempting an `IsCurrent` filter that doesn't exist for that writer kind.
- Full suite green.

## Open questions

- Exact query shape for "latest snapshot" filtering (a subquery for `MAX(snapshotMarker)`, or something
  cheaper if the marker is monotonic and indexed) — implementation detail, not a design question.
