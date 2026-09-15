# Phase 145 — SCD2 duplicate-key handling: set-based via window functions, not row-by-row

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/follow-up-phase-132-scd2-duplicate-key-window-function-optimization.md`
— the deferred option this phase carries forward, plus this doc's own from-scratch design, since neither
phase 132 nor its own plan doc worked out how the set-based version would actually be built beyond naming
`ROW_NUMBER()`/`LEAD()` as the mechanism. Replaces the row-by-row loop
`architecture/implementation/done/phase-132-cdc-guaranteed-delivery-for-scd2.md` shipped for the same
correctness guarantee — a pure implementation swap, not a behavior change.

## Why

`Scd2Writer.ApplyDuplicateKeysInOrderAsync` (the path a batch takes when `StagedChangeSet.HasChangeOrdering`
is true and at least one natural key has more than one staged row — currently only `MsSqlCdcReader`-sourced
batches) processes each duplicate key's staged rows **one at a time, in a C# loop**: per key, one query to
read back its staged rows' ordering values, then per row, two more round trips (`BuildCloseChanged` +
`BuildOpenVersions`, each scoped to that single row via a `WHERE s.OrderingColumn = @ordering` filter). For
a batch with `K` duplicate keys averaging `R` rows each, that's `K` (find-orderings) + `2 × K × R`
(close+open per row) separate statements — the correct behavior, proven by phase 132's own integration
tests, but a pass whose cost scales with the number of individual duplicate rows rather than being close
to constant.

## What this phase builds

**One set-based UPDATE + one set-based INSERT, replacing the entire per-key/per-row loop.** Both operate
over a single derived table computed once, using the same window-function trio: `COUNT(*)` (is this key a
duplicate at all — replaces `BuildFindDuplicateKeys`'s own separate round trip entirely, since it's now
computed inline rather than fetched back into C#), `ROW_NUMBER()` (each row's position in its key's own
true order), and `LEAD()` (the next staged row's timestamp — which becomes *this* row's `ValidTo`, or
`NULL` if there is no next row, meaning this row is the new current version).

```sql
;WITH DuplicateRows AS (
    SELECT s.*,
        COUNT(*) OVER (PARTITION BY {keys}) AS __DS_KeyCount,
        MIN({ChangedAtColumn}) OVER (PARTITION BY {keys}) AS __DS_FirstChangedAt,
        LEAD({ChangedAtColumn}) OVER (PARTITION BY {keys} ORDER BY {OrderingColumn}) AS __DS_NextChangedAt
    FROM {staging} AS s
)
SELECT * FROM DuplicateRows WHERE __DS_KeyCount > 1
```

- **Close whatever's pre-existing-open in target**, for every key with a duplicate — one bulk `UPDATE`
  against `{target}`, joined to `SELECT DISTINCT {keys}, __DS_FirstChangedAt FROM DuplicateRows`, using
  `__DS_FirstChangedAt` (the *first* staged row's own time, not a per-row value) as `ValidTo` — this is
  the transition from whatever the target already had open to this pass's first new version, the same
  role `BuildCloseChanged`'s existing bulk statement plays for singleton keys, just scoped to duplicate
  keys and keyed off the derived table instead of a `stagingFilter` parameter.
- **Open every new version in one INSERT**, selecting straight from `DuplicateRows` (no join back to
  target needed — every row here is, by construction, a new version): surrogate key
  `{OrderingColumn} + '|' + {SurrogateKeySource}` (unchanged from the current per-row scoping — still
  unique per row, so this still needs no pass-wide prefix), `ValidFrom = ChangedAtColumn`,
  `ValidTo = __DS_NextChangedAt`, `IsCurrent = (__DS_NextChangedAt IS NULL)`. A delete row (`s.{operation}
  = 'D'`) is excluded from the `SELECT` (mirroring `BuildOpenVersions`'s existing `<> 'D'` filter) but
  **must still participate in the `LEAD()` window** — it has no version of its own to open, but it is
  still the thing that closes whatever came before it. Get this ordering right and test it directly: a
  duplicate key ending in a delete, and a duplicate key with a delete in the middle, are both real
  scenarios `Scd2CdcGuaranteedDeliveryIntegrationTests` already covers for the row-by-row path and must
  keep covering here.

**New `HistorizedStatement` methods** (alongside, not replacing, the existing `BuildCloseChanged`/
`BuildOpenVersions`/`BuildFindDuplicateKeys`/`BuildStagedOrderingsForKey` — those still serve the singleton
path and the tests that call them directly) — exact names and signatures are an implementation detail, not
fixed here; the shapes above are the contract to hit.

**`Scd2Writer.ApplyAsync`'s own call site**: `FindKeysWithMultipleStagedRowsAsync` +
`ApplyDuplicateKeysInOrderAsync`'s current C# loop go away entirely, replaced by the two new statements —
still only run when `staged.HasChangeOrdering` (unchanged gate), and the bulk singleton-key statements'
own `BuildDuplicateKeyExclusion` filter is unaffected (it already excludes duplicate keys via a
self-referencing count, not by knowing their values, so it needs no change either way).

## Out of scope

- Any change to how duplicates are *detected* or to `ChangeOrdering` itself (phase 132's own mechanism,
  unchanged) — this phase only changes how a detected duplicate key's versions get written.
- Extending ordering/timestamp support to any reader but `MsSqlCdcReader` — unrelated, phase 132's own
  boundary, unchanged here.
- Any operator-facing toggle between row-by-row and set-based — this phase replaces the implementation
  outright; both must produce identical results for the same input, so there is nothing for a toggle to
  choose between once this lands.

## How to verify when built

- **`Scd2CdcGuaranteedDeliveryIntegrationTests` passes unchanged** — same assertions, same scenarios
  (duplicate key with a real value transition every time, a duplicate key whose middle change touches no
  mapped column, a singleton key in the same pass, per phase 132's own test doc comment). This is the
  correctness bar: the set-based rewrite must be behaviorally identical, not merely "also correct."
- **New test scenarios this phase should add**, since they were never load-bearing for phase 132's own
  row-by-row correctness but are new edge cases in a window-function `LEAD()`/`ROW_NUMBER()` rewrite: a
  duplicate key's *first* staged row also being a delete (nothing to close in target, nothing new to open
  from that row, but it still needs to not break `MIN(ChangedAtColumn)`); a duplicate key's *last* staged
  row being a delete (closes the prior version, opens nothing — the key ends this pass with no open
  version at all, unlike every other duplicate-key scenario).
- **A real before/after comparison**, not just "it's obviously fewer round trips": run a batch with a
  meaningful number of duplicate keys (say, 50 keys × 5 staged rows each) through both implementations
  (temporarily side by side, or via git stash/checkout) against a real server, and record actual wall time
  or round-trip count — the follow-up doc this phase came from explicitly says this was never measured,
  and "it's obviously better" shouldn't be the only evidence in the retrospective.

## Open questions to resolve during implementation

- **Exact dialect portability of the window-function SQL above.** `ROW_NUMBER()`/`LEAD()`/`COUNT() OVER`
  are ANSI SQL and both SQL Server and PostgreSQL support the syntax sketched here identically. Checked:
  `ROW_NUMBER() OVER (PARTITION BY ...)` already has one precedent in this codebase
  (`src/DbDataSync.State/TaskRunStore.cs`), but that's the state store's own SQLite dialect, a separate
  abstraction from `SqlDialect` (the replication source/target engine layer `HistorizedStatement` itself
  uses) — `LEAD()` and window functions generally have no existing precedent in `SqlDialect`'s own
  abstraction, so this phase is that layer's first use of the pattern, not a proven-safe repeat of one.
- **Whether the "does any duplicate exist at all" check stays a cheap up-front guard** (skip both new
  statements entirely when there's nothing for them to do, matching row-by-row's own "singleton keys pay
  nothing extra" property) or whether the `WHERE __DS_KeyCount > 1` filter inside the derived table is
  itself cheap enough that a separate guard query would cost more than it saves — worth measuring, not
  assuming either way.
