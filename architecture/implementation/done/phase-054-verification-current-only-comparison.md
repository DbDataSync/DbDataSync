# Phase 54 — "Current only" comparison for verification checks against historized targets

**Status**: Done
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

---

# Retrospective

A check against an SCD Type 2 or Snapshot target can now compare like with like. Before this, one
against a historized target reported a difference on every run — the feature working, shown as a
defect, which is the fastest way to teach an operator that a screen is wrong.

## Two shapes, not one assumed universally

The plan flagged this and it turned out to be the whole of the design. SCD Type 2 has a per-row flag:
one version of each key is current and the rest are closed, so the filter is a comparison. A Snapshot
target has no such thing — every snapshot is a complete separate copy — so "current" means the newest
marker value, and the filter is a subquery.

Offering SCD2's shape for both would produce a filter against a column that does not exist. The editor
offers the option only where the writer historizes, and pre-fills the column from the writer's own
constant.

## The parenthesis is the bug that was not written

A check's own filter is admin-authored raw SQL, and the current-only predicate is `AND`ed onto it. A
filter containing an `OR` — `Region = 'north' OR Region = 'south'` — would bind looser than the `AND`
and silently compare every version again, reporting a difference that is not one. Wrapping the
operator's filter in parentheses is one character each side and the difference between a correct
answer and a plausible wrong one.

That changed an existing assertion, which is how it got recorded rather than slipped in.

## The source is never filtered

A source has no history to filter, by definition — and filtering it too would compare a subset of the
source against all of the target, which is the same mistake pointing the other way. It is the mistake a
symmetric implementation makes without anybody deciding to, so it has its own test.

## Additive, and asserted as such

A check that does not ask for this builds exactly the statement it built before — asserted directly,
because "additive" is a claim about existing behaviour and this feature touches the statement builder
every check goes through.

## Verification

- `CurrentOnlyComparisonTests` (8) — the SCD2 flag predicate, the Snapshot most-recent subquery, no
  column meaning no predicate, the statement unchanged without the option, the target narrowed with it,
  a check's own filter parenthesised before the `AND`, a Sum check narrowed the same way, and the
  boolean literal coming from the dialect.
- `CurrentOnlyExecutionTests` (5) — the target narrowed and **the source not**, neither narrowed
  without the option, a snapshot target's different shape, nothing narrowed when the option is set with
  no column named, and a Sum check.
- The SCD2 and Snapshot behaviours these filter over are phase 55's integration tests, against a real
  database.

## Open questions

- ~~**The query shape for "latest snapshot".**~~ `= (SELECT MAX(marker) FROM …)`, rather than a join or
  a window function: the marker is one value per pass, so the planner reads it once and the generated
  SQL says what it means to somebody reading it.
- **New**: nothing validates that the named current-row column exists on the target. A typo produces a
  failed check with the engine's own "invalid column" message, which is honest but arrives one run
  later than it needs to — the catalog is already loaded for the mapping's columns elsewhere.
