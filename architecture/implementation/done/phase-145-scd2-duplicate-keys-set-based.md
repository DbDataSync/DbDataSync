# Phase 145 — SCD2 duplicate-key handling: set-based via window functions, not row-by-row

**Status**: Implemented 2026-09-16. Unit-tested locally; the behavioural bar
(`Scd2CdcGuaranteedDeliveryIntegrationTests`, which needs a real SQL Server with CDC) is verified by the
`dotnet-integration` CI job, not on the implementing machine — see "How this was verified" for exactly
what that means and what is still unmeasured.
**Plan reference**: `architecture/planning/done/follow-up-phase-132-scd2-duplicate-key-window-function-optimization.md`
— the deferred option this phase carried forward. Replaces the row-by-row loop
`architecture/implementation/done/phase-132-cdc-guaranteed-delivery-for-scd2.md` shipped for the same
correctness guarantee: a pure implementation swap, not a behavior change.

## Why

`Scd2Writer.ApplyDuplicateKeysInOrderAsync` (the path a batch took when `StagedChangeSet.HasChangeOrdering`
was true and at least one natural key had more than one staged row — currently only `MsSqlCdcReader`-sourced
batches) processed each duplicate key's staged rows **one at a time, in a C# loop**: per key, one query to
read back its staged rows' ordering values, then per row, two more round trips. For a batch with `K`
duplicate keys averaging `R` rows each, that was `1 + K + 2KR` statements.

It is now **three**, whatever `K` and `R` are: one existence check, one `INSERT`, one `UPDATE`.

## What was built

`HistorizedStatement.BuildOpenDuplicateKeyVersions` and `BuildCloseDuplicateKeyVersions`, both rendering
the same three-CTE derived table (`BuildDuplicateKeyCte`), and a `Scd2Writer.ApplyDuplicateKeysAsync`
that issues them in that order. `FindKeysWithMultipleStagedRowsAsync` became
`AnyKeyHasMultipleStagedRowsAsync` — the same query, read for existence instead of for key values.

### The plan's own SQL sketch was wrong, and this is the part worth reading

The sketch in this doc's original `todo/` version — a single `LEAD(ChangedAt)` over every staged row,
with "no join back to target needed — every row here is, by construction, a new version" — **does not
reproduce what the row-by-row loop does**, and would have failed this phase's own stated correctness bar
on two scenarios, one of which the existing integration test already covers:

1. **A staged change that touches no mapped column is not a new version.** `Scd2CdcGuaranteedDeliveryIntegrationTests`'
   Id 3 stages three rows, the middle one an update to the unmapped `Note` column. The loop opened
   nothing for it, via `BuildOpenVersions`' own `NOT EXISTS (open version)` guard. The sketch would have
   inserted a second, identical `'y'` version — `["x","y","y","z"]` instead of `["x","y","z"]`, and
   `RowsWritten` 6 instead of 5. The test would have caught it; the design would not have.
2. **Closing the pre-existing version at `MIN(ChangedAt)` is too early.** The sketch closed every
   duplicate key's open version at its *first staged row's* time. The loop closes it at the first row
   that is a delete or actually differs from it. A key whose first staged change touches no mapped
   column (the same shape as 1, moved to the front) would have had its open version ended by a change
   that replaced nothing, and then a duplicate of itself opened over the gap.

**What replaced it.** The loop's per-key state — "the version currently open for this key" — collapses
to something purely local, and proving that is what made the rewrite possible:

> A row either opened its own version or matched the one already open. So **the previous staged row's
> values are always the currently-open version's values.** Only the first row of a key has no
> predecessor, and that one alone has to consult the target. A delete leaves no open version behind, so
> the row after a delete always opens, whatever its values.

So the mechanism is `LAG`, not just `LEAD`: each row is compared against the row before it (null-safely,
the same comparison `BuildCloseChanged` uses), and only `ROW_NUMBER() = 1` reaches the target. A row that
changes nothing is not a *boundary*; `LEAD` then runs over the boundary rows only, which is what gives a
version its correct `ValidTo` across intervening no-op rows. `COUNT(*) OVER (PARTITION BY key)` scopes the
whole thing to duplicate keys, as the sketch intended.

### The statement-ordering hazard, which the plan did not anticipate at all

Two statements, both needing the **pre-pass** answer to "was a version open for this key, with these
values", and the first one writes to the target the second one reads. Every ordering breaks naively:

- **Close first**: the open statement then sees a key whose version has just been closed, so its first
  row finds no open version to match and opens a second, spurious copy of it.
- **Open first**: the close statement then sees the versions just inserted — as `IsCurrent` rows that
  match the key — and closes them.

Fixed by making both statements ignore rows *this pass itself opened*, matched on the surrogate key.
That key is `{OrderingColumn}|{naturalKey}` — a pure function of staged data — so it can be recomputed
from the staging table alone, with nothing carried between the statements. Both renderings of the derived
table are then identical whichever order they run in, and the order became a free choice (open first, so
the update that follows has fewer rows to consider).

### Removed, not kept alongside

The original plan said to keep the phase 132 builders "alongside, not replacing". Two of them were kept
because they still do something — `BuildFindDuplicateKeys` (now the existence guard) and
`BuildDuplicateKeyExclusion` (still on the bulk statements). Two were **deleted**, because nothing builds
them any more and a statement builder that nothing calls is a misleading surface, not a spare part:

- `BuildStagedOrderingsForKey` — existed only to drive the per-row loop.
- `BuildOpenVersions`' `versionKeyPrefixExpression` parameter — the per-row surrogate override, which
  only the loop ever passed. The bulk statement always used the pass-wide prefix and still does.

`DbDataSync.Drivers.Generic` is not a packaged assembly (no `PackageId`; only the CLI tool is packed), so
this breaks no published contract. Their tests went with them; the replacements are in the same file.

## Open questions, resolved

**Dialect portability.** `COUNT(*) OVER`, `ROW_NUMBER()`, `LAG` and `LEAD` are ANSI and identical on SQL
Server and Postgres, and everything engine-specific goes through `SqlDialect` as usual (`TrueLiteral` /
`FalseLiteral` for the `BIT`-vs-`boolean` split, `Concat` for `+`-vs-`||`, `CastToText`). A CTE preceding
`INSERT … SELECT` and preceding `UPDATE` is valid on both. Pinned by
`Scd2WriterTests.TheSameStatementsAreBuiltForAnotherDialect`, which builds the whole statement against
`ColonDialect` and asserts no SQL Server spelling survives. **Only SQL Server actually executes it today**:
`MsSqlCdcReader` is still the only reader that produces an ordered batch, so Postgres portability is
asserted in the statement text and not yet by a running server.

**Whether to keep a cheap up-front guard**, or let `WHERE __DS_KeyCount > 1` do the filtering with no
separate query. **Kept — and the reason is not the duplicate statements, it is the bulk ones.**
`BuildDuplicateKeyExclusion` is only worth adding to the bulk close/open when there is something to
exclude; adding it unconditionally would make every ordinary CDC pass — the overwhelming majority, which
has no duplicate key at all — pay for a correlated count per staged row that can never exclude anything.
Since the answer is needed regardless, the duplicate statements may as well be guarded by it too. The
query is unchanged from phase 132's; only its consumer got cheaper (existence, not every key's values
marshalled back over the wire).

## How this was verified

- **`Scd2WriterTests` rewritten and green** (24 tests, local). These pin the window-function logic as
  text: that only the first row of a key consults the target, that every other row compares against its
  predecessor null-safely, that a delete is a boundary but opens nothing, that a version's `IsCurrent`
  comes from having no next boundary, that both statements carry the this-pass exclusion, that a
  composite key partitions and joins on every part, and that a key-only table renders `1 = 0` rather than
  an empty `OR`. Each of these is a real defect the rewrite had available to it and is readable in the
  SQL, which is why they are unit tests rather than integration ones.
- **The full non-integration suite is green** on everything this phase touches.
- **`Scd2CdcGuaranteedDeliveryIntegrationTests` is the behavioural bar**, and it runs in CI's
  `dotnet-integration` job against real SQL Server 2022 containers with CDC enabled — not on the machine
  this was written on, which has no Docker. The existing test is unchanged, which is the point: the
  rewrite has to be behaviourally identical, not merely also correct.
- **Two new integration scenarios added**, as this phase's plan asked for, both new edge cases in a
  window-function rewrite that the row-by-row loop got for free from its own per-row state:
  a duplicate key whose **first** staged row is a delete (Id 4, deleted and re-inserted between reads —
  the delete must stay in the ordering while being excluded from the insert, and the row after it must
  open regardless of its values), and one whose **last** staged row is a delete (Id 5, updated then
  deleted — the key ends the pass with no open version at all, and the version this pass opened is
  opened already closed).

### What is not verified

**No before/after timing was measured**, which is exactly what the follow-up doc this phase came from
complained about last time, so it is named rather than glossed. The round-trip count is countable by
construction and is not in doubt — `1 + K + 2KR` statements became 3, and the loop that produced the
first number is gone — but "fewer round trips" is not the same claim as "faster against a real server
with a real plan", and the window functions do add a sort the loop did not pay for. Measuring it needs a
server, which this machine does not have.

Carried forward as its own doc rather than as a paragraph here, per this folder's own rule:
`architecture/planning/todo/follow-up-phase-145-set-based-scd2-duplicates-never-timed.md`.

## Out of scope, unchanged

- How duplicates are *detected*, and `ChangeOrdering` itself (phase 132's mechanism).
- Extending ordering/timestamp support to any reader but `MsSqlCdcReader`.
- Any operator-facing toggle between row-by-row and set-based. There is nothing left to toggle: the
  row-by-row implementation is gone, and both produced identical results for the same input, which is
  the property the integration test exists to hold.
