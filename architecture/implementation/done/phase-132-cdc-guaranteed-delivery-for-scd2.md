# Phase 132 — CDC-sourced source change ordering, and per-key batching in SCD2

**Status**: Complete.
**Plan reference**: `architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md`,
Follow-up 2 (Follow-up 1 — row-bounded CDC reads — shipped separately as phase 84). Builds on phase 55
(`Scd2Writer`) and phase 32 (`PositionExpiredException`, unchanged by this). Resolves the plan doc's
four open questions; see "Open questions," below.

## Why, confirmed against the shipped code

The plan doc's own investigation (2026-08-30) already nailed the mechanism down precisely — re-verified
here, not re-derived:

`Scd2Writer.ApplyAsync` closed and opened versions with two set-based statements
(`HistorizedStatement.BuildCloseChanged`/`BuildOpenVersions`), keyed by the mapping's natural key, with
the surrogate key built from `{prefix}|{naturalKey}` where `prefix` is **one timestamp for the whole
pass**. `MsSqlCdcReader`'s `fn_cdc_get_all_changes_*` function (confirmed: `DetectsDeletes => true`, and
unlike Change Tracking it returns every individual DML row, not one net row per key) can stage more than
one row for the same key in a single pass — a row changed twice between reads is CDC's ordinary output,
not an edge case. When that happens, both staged rows computed the *identical* surrogate key (same pass,
same natural key) and the `INSERT` collided on the target's own primary key, failing the run
deterministically on retry.

## What was built

### 1. A per-row source change ordering and timestamp — `MsSqlCdcReader`

`ChangeOrdering` (new, `DbDataSync.Drivers.Abstractions`), beside `BoundedRead`'s own well-known-column
convention:

```csharp
public static class ChangeOrdering
{
    public const string OrderingColumn = "__DS_ChangeOrdering";
    public const string ChangedAtColumn = "__DS_ChangedAtUtc";

    public static bool IsPresent(ChangeSchema schema) => ...;
    public static Task<(bool HasChangeOrdering, IAsyncEnumerable<ChangeRow> Rows)> DetectAsync(
        IAsyncEnumerable<ChangeRow> rows, CancellationToken cancellationToken) => ...;
}
```

`IChangeReader.CapturesChangeOrder => false` (default interface member, same shape as `DetectsDeletes`).
`MsSqlCdcReader.CapturesChangeOrder => true`.

`MsSqlCdcStatement.BuildRead` projects both columns unconditionally, appended right after the mapped
columns and ahead of any bounded position column, in both the unbounded and bounded (`WITH TIES`)
shapes, in both `NetChanges` and `AllChanges` modes:

```sql
CONVERT(varchar(20), __$start_lsn, 2) + CONVERT(varchar(20), ISNULL(__$seqval, 0x00000000000000000000), 2)
    AS __DS_ChangeOrdering,
sys.fn_cdc_map_lsn_to_time(__$start_lsn) AS __DS_ChangedAtUtc
```

`NetChanges` cannot select `__$seqval` at all (the function does not return it — a distinct "Invalid
column name" trap from the one the existing `ORDER BY` already avoided), so the seqval half is a literal
`CONVERT(varchar(20), 0x00000000000000000000, 2)` in that mode rather than an `ISNULL` over a column
that isn't there. `MsSqlCdcReader.ReadIncrementalAsync`'s schema is now
`[.. mapped columns, ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]` — real,
schema-visible columns, unlike `BoundedRead.PositionColumn`, which stays out-of-band. The existing
`i + 1` (past `__$operation`) value-reading loop needed no change: it already reads exactly
`schema.Count` columns, and schema now simply reports two more of them.

### 2. Staging persists the two columns when a row's schema has them

Both `MsSqlStagingTableProvider` and `BatchInsertStagingProvider` inspect the first staged row's schema
for both `ChangeOrdering` columns by name — no new interface member, no new `options` entry — and add
two matching nullable staging columns (`ChangeOrdering.OrderingColumn`/`ChangedAtColumn` in the target
dialect's own `ChangeOrderingColumnType`/`ChangedAtColumnType`, two new `SqlDialect` virtual properties:
`VARCHAR(64)`/`DATETIME2` by default, Postgres overriding the latter to `timestamp`) when found, copying
them through with the mapped columns; absent, staging is byte-for-byte what it already was.

`StagedChangeSet` gained a defaulted `bool HasChangeOrdering = false` — non-breaking to every existing
construction site.

**The real complication the plan doc's own wording did not anticipate**: every staging provider builds
its `CREATE TABLE` *before* it reads a single row (`MsSqlStagingTableProvider` from the target's live
catalog, `BatchInsertStagingProvider` from the cached target columns) — so "does this pass's schema
carry the two columns" cannot be answered by inspecting an already-read row; it has to be answered from
the very first row, before it is truly consumed, with that row still delivered to whatever reads the
sequence afterwards. `ChangeOrdering.DetectAsync` is a peek-and-replay: it takes one item off the
caller's `IAsyncEnumerable<ChangeRow>` by hand, inspects its schema, and hands back a wrapped sequence
that yields that row first and then drains the original enumerator. Both staging providers call it once,
at the top of `StageAsync`, before building their `CREATE TABLE`. `MsSqlStagingTableProvider.BulkCopyAsync`
needed no change to `ChangeRowDataReader` at all to carry the two extra columns through `SqlBulkCopy`:
they are self-mapped (same name in the reader's schema as in the staging table), and
`ChangeRowDataReader` already resolves a "target" column's ordinal by looking up
`sourceColumnByTarget[targetColumn]` against the row's own schema — an identity entry does exactly what
a real mapping would.

### 3. `Scd2Writer` — the actual fix

Exactly the design chosen in planning: per-key row-by-row processing, composed from the *existing,
already-tested* single-row statement shapes, not a rewrite and not row-by-row for every row.

`HistorizedStatement.BuildCloseChanged`/`BuildOpenVersions` gained optional trailing parameters —
`validToExpression`/`validFromExpression` (default: the pass-wide `@now` parameter, unchanged) and
`stagingFilter` (default: none, unchanged) — used two ways:

- **Bulk statement, duplicate-key exclusion.** `HistorizedStatement.BuildDuplicateKeyExclusion` renders
  `(SELECT COUNT(*) FROM {staging} AS dup WHERE dup.key = s.key) = 1` — a self-referencing count, not a
  list of literal key values, so the bulk statement's own shape never depends on how many keys happened
  to collide this pass, and a composite key never has to be rebuilt as a parameter list. Passed as
  `stagingFilter` only when `FindKeysWithMultipleStagedRowsAsync` (`HistorizedStatement.BuildFindDuplicateKeys`
  — one `GROUP BY ... HAVING COUNT(*) > 1`) found at least one duplicate key; the common case (no
  duplicates) pays nothing extra, not even the exclusion clause's own text.
- **Per-row, single-row scoping.** For each duplicate key, `HistorizedStatement.BuildStagedOrderingsForKey`
  reads back that key's staged rows' `OrderingColumn` values, in order. Each is then processed with
  `stagingFilter = "s.{OrderingColumn} = @ordering"` — sufficient on its own, because `OrderingColumn` is
  unique per row across the *whole* staged batch, not merely within one key, so no additional key
  parameters need to be re-bound into the statement.

`BuildCloseChanged`'s `ValidTo` needed a correlated **scalar** subquery when `validToExpression` is
supplied — the `UPDATE`'s `SET` clause has no row alias of its own to read a staged column from, unlike
`BuildOpenVersions`'s `SELECT`, which already iterates per staged row and can simply select the column
straight through. The subquery is safe as a scalar precisely because it can only ever match one row: the
`stagingFilter` scopes it to exactly one (the per-row path), or the caller is the bulk statement, which
by construction only reaches keys with exactly one staged row.

`staged.HasChangeOrdering ? ChangedAtColumn : "@now"` is used for **every** row's `ValidFrom`/`ValidTo`,
not only duplicate keys' — a real per-row source time is strictly better than the pass time whenever
it's available. The surrogate key for a duplicate key's own row is `s.{OrderingColumn} + '|' + naturalKey`
(the `+`/`||` concatenation the dialect already renders) rather than `{prefix}{naturalKey}` — unique per
row by construction, so two versions of one key opened in the same pass can no longer collide. Every
other key keeps the pass-wide prefix, unchanged.

### 4. `FindKeysWithMultipleStagedRowsAsync`

`Scd2Writer.FindKeysWithMultipleStagedRowsAsync` runs `HistorizedStatement.BuildFindDuplicateKeys`
(`SELECT keys FROM staging GROUP BY keys HAVING COUNT(*) > 1`) only when `staged.HasChangeOrdering`, and
reads back the actual key values (not merely a count) — `ApplyDuplicateKeysInOrderAsync` needs them to
look up each key's own staged rows via `BuildStagedOrderingsForKey` afterwards.

## What this does not build

Exactly as scoped — none of these were built:

- **The window-function, fully-set-based alternative for duplicate keys.** Real, weighed, not chosen.
- **A `guaranteed-delivery` mode, config toggle, or capability declaration an operator sets.**
  `IChangeReader.CapturesChangeOrder` is automatic, the same way `DetectsDeletes` already is.
- **Ordering/timestamp support for any reader but `MsSqlCdcReader`.**
- **Transactional-integrity-across-a-split as a new guarantee.** Unchanged from phase 84.

## Decisions made, and real bugs found

- **The peek-and-replay staging pattern (`ChangeOrdering.DetectAsync`) was not in the original design.**
  The plan doc's "inspect each `ChangeRow.Schema` for both column names by presence" reads as if a
  staging provider could simply look at a row it already has — but both providers build their `CREATE
  TABLE` before reading any row at all, precisely so the table exists before the bulk copy / insert loop
  starts. Answering "does this schema have the columns" therefore requires consuming (and then
  re-delivering) the first row before the table is created. This is the one place that peek-and-replay
  happens, shared by both providers rather than each inventing its own.
- **`ChangeRowDataReader` needed no change**, contrary to an initial assumption while planning the
  `MsSqlStagingTableProvider` side. The two ordering columns are self-mapped (same name on both sides),
  and the existing `sourceColumnByTarget` dictionary already supports an identity entry — so
  `BulkCopyAsync` just extends its column list and mapping dictionary with two more entries pointing at
  themselves, rather than teaching the reader class a new "passthrough column" concept.
- **`BuildCloseChanged`'s `ValidTo` needed a correlated scalar subquery**, not a bound parameter, once a
  per-row source time was wanted for the bulk statement too (not just the duplicate-key path) — the
  `UPDATE ... SET` clause has no staging-row alias to read a column from directly. Confirmed safe as a
  scalar subquery by the same reasoning that makes the bulk statement correct in the first place: it
  only ever runs against keys already known to have exactly one staged row.
- **A real, reproducible flaw found writing the integration test, not in the shipped code**: an early
  version of the reproduction test captured an "expected" per-row time by calling
  `MsSqlCdcCatalog.MapLsnToTimeAsync` independently, right after each source change, and compared it
  against what the writer had persisted. This failed — not because the writer was wrong, but because
  `sys.fn_cdc_map_lsn_to_time` **interpolates** between points `cdc.lsn_time_mapping` holds (its own doc
  comment says as much), and a *later* `sp_cdc_scan` can add new mapping points that shift the
  interpolation for an *already-committed* LSN. Querying the same LSN's mapped time before and after
  several more scans can legitimately return values a few milliseconds apart. The test was rewritten to
  assert internal consistency instead — the closing edge and opening edge of one transition share the
  exact same value (both come from the one staged row's own `ChangedAtColumn`), and consecutive
  transitions do not share a value with each other (which a single pass-wide `@now` would produce) —
  which is what "a real per-row source time, not the pass time" actually needs to prove, without
  depending on the interpolation function being stable across scans. Worth recording because it is a
  real property of `sys.fn_cdc_map_lsn_to_time` this codebase now knows and did not before.
- **A CDC-specific test-fixture trap, also found while writing the reproduction test**: a full load's
  own watermark is fixed to the *current* max LSN as of the read, and CDC has separately captured the
  rows the full load's own seed `INSERT` produced (they happened after capture was enabled). Reading
  incrementally from that watermark without scanning first re-delivers those inserts as phantom "change"
  rows on the very next pass — for every key, not just the ones a test means to exercise as duplicates.
  This turned an intended singleton key into an accidental duplicate in an early version of the test.
  The fix — scan once, right before the full-load read, so the watermark it fixes already reflects the
  post-insert log position — is the same "settle first" technique `MsSqlCdcReaderTests` already uses for
  its own first-pass re-delivery, applied one step earlier than that suite needed it.
- **`OrderingColumn|naturalKey` uses an explicit `'|'`**, unlike the pass-wide prefix (which has its
  separator baked into the prefix string itself, `yyyyMMddHHmmssfff-`). `OrderingColumn`'s hex text has
  no such trailing character, so the separator is rendered explicitly in the concatenation expression.
  Cosmetic, not a design change from what the doc described.

## How it was verified

- **`MsSqlCdcStatementTests`** (+8): both new columns in the unbounded and bounded shapes; `NetChanges`'s
  literal-zero seqval half and the absence of any `__$seqval` reference; `AllChanges`'s real,
  `ISNULL`-guarded seqval; the two existing exact-`SELECT`-list assertions updated for the two extra
  columns landing between the mapped columns and the (bounded-only) position column; ordinal math
  (`PositionOrdinal`) against a schema built the way the reader actually builds it, cap on and off.
- **`ChangeOrderingTests`** (new, `DbDataSync.Drivers.Abstractions.Tests`, 6): `IsPresent` true only with
  both columns; `DetectAsync` peeks correctly, replays every row in order including the one peeked, and
  answers `false` (not throwing) on an empty sequence.
- **`StagingStatementTests`** (+4): `BuildCreate`/`BuildInsert` with `includeChangeOrdering: true` add
  both columns ahead of the operation marker; both are byte-for-byte identical to before this phase when
  omitted.
- **`MsSqlStagingTableProviderChangeOrderingTests`/`BatchInsertStagingProviderChangeOrderingTests`**
  (new, `Category=Integration`, real SQL Server — both, since only a live server can prove the temp-table
  DDL a catalog-driven provider builds): a schema with both columns stages them, round-trips the exact
  values, and reports `HasChangeOrdering: true`; a schema without them reports `false` and the staging
  table genuinely has no such column (`SELECT` of it throws "Invalid column name" — proof of absence,
  not merely a flag).
- **`Scd2WriterTests`** (new, `DbDataSync.Drivers.Generic.Tests`, 12): the duplicate-key exclusion
  clause; the single-row-scoped close statement's correlated `ValidTo` subquery; the single-row-scoped
  open statement's ordering-based surrogate key and per-row `ValidFrom`; every optional parameter's
  default reproducing the pre-phase statement byte-for-byte; the full single-row-scoped shape asserted
  as one exact string; the real `MsSqlDialect`'s `+` concatenation confirmed once directly (the portable
  tests use the generic-layer's `BracketDialect` test double, whose `Concat` is ANSI `||`, not SQL
  Server's `+`, since it does not override it — the same reason the pre-existing composite-natural-key
  test in `HistorizedStatementTests` already saw `||`).
- **`Scd2CdcGuaranteedDeliveryIntegrationTests`** (new, `Category=Integration`, real SQL Server, CDC
  enabled with `@supports_net_changes = 0` so all-changes is used and repeated updates are never
  collapsed): one pass, three keys — a duplicate key with two real value transitions (no PK violation;
  the closing/opening edges of each transition share one exact value; the two transitions do not share a
  value with each other; the surrogate key is `OrderingColumn|naturalKey` and the two versions' keys
  differ), a duplicate key with three staged changes where the middle touches only an unmapped column
  (two versions opened, not three), and a singleton key in the very same pass (still the pass-wide
  prefix format, proving the bulk path is genuinely untouched for it). `WriteResult.RowsWritten` (5, not
  the 7 staged rows) confirmed directly. Stable across repeated runs.
- **Regression**: `Scd2NaturalKeyIntegrationTests` (7 tests) and `ReconcileDeletesScd2IntegrationTests`
  (1 test) green, unchanged — neither pairs `Scd2Writer` with `MsSqlCdcReader`, so `HasChangeOrdering`
  never engages the new path for them.
- **Full suite for every touched assembly**: `DbDataSync.Drivers.Abstractions.Tests` 68 passed;
  `DbDataSync.Drivers.Generic.Tests` 191 passed; `DbDataSync.Drivers.MsSql.Tests` 261 passed
  (`Category=Integration` and `!=Integration` together); `DbDataSync.Drivers.Postgres.Tests` 64 passed
  (including `HistorizedWriterTests` against a real Postgres server — `Scd2Writer`/`HistorizedStatement`
  are engine-neutral, and this is what proves the phase's optional parameters didn't change Postgres's
  own statement shapes when unused); `DbDataSync.Drivers.DuckDb.Tests` 33 passed (the two new
  `SqlDialect` virtual properties, defaulted, needed no DuckDb override); `DbDataSync.TaskRunner.Tests`
  77 passed; `DbDataSync.Api.Tests` full suite passed. No failures anywhere.

## What's explicitly still not built

Same boundary as planned: the window-function alternative for duplicate keys remains a real, deferred
optimization, now written up in
`architecture/planning/todo/follow-up-phase-132-scd2-duplicate-key-window-function-optimization.md` — the row-by-row design
shipped here is correct and only pays its extra cost when a key actually has more than one staged row.
No operator-facing toggle exists or was ever intended.
Ordering/timestamp support stays exclusive to `MsSqlCdcReader`. A source transaction spanning a read
boundary is still not guaranteed to land in one pass — unchanged from phase 84, and not attempted here.

## Open questions — resolved

1. *"Is the same-key collision a real, reproducible defect?"* — confirmed twice over: by the plan doc's
   code inspection, and now by a real integration test that reproduces the exact collision shape and
   confirms the fix prevents it.
2. *"Should the same-key-collision fix ship on its own, ahead of the broader guaranteed-delivery mode?"*
   — moot, as designed: there is no separate "narrow fix" and "broad mode." Shipped as one phase.
3. *"Cap batches to one change per key upstream, or make the writer process duplicates in order?"* — the
   writer, as leaned.
4. *"Exact mechanism for declaring/enforcing the guarantee?"* — `IChangeReader.CapturesChangeOrder` →
   `StagedChangeSet.HasChangeOrdering`, both automatic, no operator-facing toggle.

## References

- `architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md` — the confirmed
  defect and the two candidate fixes this phase chose between.
- `architecture/implementation/done/phase-084-cdc-row-bounded-reads.md` — Follow-up 1, already shipped;
  this phase's `BoundedRead.PositionColumn` precedent for an out-of-band vs. schema-visible extra column.
- `src/DbDataSync.Drivers.Generic/Scd2Writer.cs`, `HistorizedStatement.cs` — the statements this phase
  reused rather than replaced, and where the duplicate-key machinery now lives.
- `src/DbDataSync.Drivers.MsSql/MsSqlCdcReader.cs`, `MsSqlCdcStatement.cs`,
  `MsSqlCdcCatalog.MapLsnToTimeAsync` — the existing LSN-to-time mechanism this phase applies per row
  instead of only at the pass's ending position, and the interpolation-drift behavior found while testing
  it.
- `src/DbDataSync.Drivers.Abstractions/ChangeOrdering.cs`, `StagedChangeSet.cs` — the two extension
  points this phase's plumbing turned out to need: the well-known column names plus the peek-and-replay
  staging helper, and the additive `StagedChangeSet.HasChangeOrdering` field.
