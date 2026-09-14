# Phase 132 — CDC-sourced source change ordering, and per-key batching in SCD2

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md`,
Follow-up 2 (Follow-up 1 — row-bounded CDC reads — shipped separately as phase 84). Builds on phase 55
(`Scd2Writer`) and phase 32 (`PositionExpiredException`, unchanged by this). Resolves the plan doc's
four open questions; see "Open questions," below.

## Why, confirmed against the shipped code

The plan doc's own investigation (2026-08-30) already nailed the mechanism down precisely — re-verified
here, not re-derived:

`Scd2Writer.ApplyAsync` closes and opens versions with two set-based statements
(`HistorizedStatement.BuildCloseChanged`/`BuildOpenVersions`), keyed by the mapping's natural key, with
the surrogate key built from `{prefix}|{naturalKey}` where `prefix` is **one timestamp for the whole
pass** (`Scd2Writer.cs:87-90`: `var now = ...; var prefix = $"{now:...}-";`). `MsSqlCdcReader`'s
`fn_cdc_get_all_changes_*` function (confirmed: `DetectsDeletes => true`, and unlike Change Tracking it
returns every individual DML row, not one net row per key) can stage more than one row for the same key
in a single pass — a row changed twice between reads is CDC's ordinary output, not an edge case. When
that happens, both staged rows compute the *identical* surrogate key (same pass, same natural key) and
the `INSERT` collides on the target's own primary key, failing the run deterministically on retry.

## What this builds

### 1. A per-row source change ordering and timestamp — `MsSqlCdcReader`

Two new well-known column names, `DbDataSync.Drivers.Abstractions` (beside `BoundedRead`'s own
well-known-column convention):

```csharp
/// <summary>Column names a reader appends to its own <see cref="ChangeSchema"/>, beyond the mapped
/// business columns, when it can state the true source order and time of each individual change —
/// phase 132. A staging provider that finds both by name in a row's schema persists them as real
/// staging columns and reports <see cref="StagedChangeSet.HasChangeOrdering"/>; a writer that finds
/// that flag set can process a key with more than one staged row correctly instead of colliding.</summary>
public static class ChangeOrdering
{
    /// <summary>Sortable as text, unique per row, in true source order — CDC's
    /// <c>(__$start_lsn, __$seqval)</c> rendered as fixed-width hex, so two rows never share one even
    /// within the same transaction.</summary>
    public const string OrderingColumn = "__DS_ChangeOrdering";

    /// <summary>The wall-clock time the engine associates with this row's change — CDC's
    /// <c>sys.fn_cdc_map_lsn_to_time(__$start_lsn)</c>. Best-effort: the function interpolates between
    /// points the capture job recorded, so two rows in the same transaction share this value even
    /// though <see cref="OrderingColumn"/> tells them apart. Never the pass time.</summary>
    public const string ChangedAtColumn = "__DS_ChangedAtUtc";
}
```

`IChangeReader` gains `bool CapturesChangeOrder => false;` (a default interface member, the same shape
`DetectsDeletes` and phase 129e's `ContractVersion` already use for a per-reader capability flag).
`MsSqlCdcReader.CapturesChangeOrder => true`.

`MsSqlCdcStatement.BuildRead` projects both columns unconditionally (both CDC functions expose
`__$start_lsn`; only `AllChanges` also has `__$seqval` — `NetChanges` already collapses to one row per
key, so a literal zero stands in for its missing seqval and the columns are simply never ambiguous
there):

```sql
CONVERT(varchar(20), __$start_lsn, 2) + CONVERT(varchar(20), ISNULL(__$seqval, 0x00000000000000000000), 2)
    AS __DS_ChangeOrdering,
sys.fn_cdc_map_lsn_to_time(__$start_lsn) AS __DS_ChangedAtUtc
```

appended to both the unbounded and bounded (`WITH TIES`) shapes, in the same position relative to the
mapped columns and existing `BoundedRead.PositionColumn` that `PositionOrdinal` already accounts for —
`ReadIncrementalAsync`'s ordinal math needs updating for the two new columns the same way it already
accounts for the position column, and `ChangeSchema` (built from `columns` today) needs to include
these two names so they reach `ChangeRow.Values` as real, schema-visible data — unlike the position
column, which stays out-of-band (read at a fixed extra ordinal, used only to track `bounded.Reached`,
never part of `ChangeRow` at all).

### 2. Staging persists the two columns when a row's schema has them

`MsSqlStagingTableProvider`/`BatchInsertStagingProvider` (`StageAsync`) inspect the schema of the rows
they're given for `ChangeOrdering.OrderingColumn`/`ChangedAtColumn` by name — no new interface member,
no new `options` entry: `StageAsync` already receives `IAsyncEnumerable<ChangeRow>` directly, and each
`ChangeRow.Schema` already carries whatever the reader actually produced. When both are present, the
staging table gets two matching columns (nullable, since a delete's non-key-column values are already
null by the reader's own existing convention) and copies them through with the mapped columns; when
absent — every reader but `MsSqlCdcReader` today — staging is byte-for-byte what it already is.

`StagedChangeSet` (`sealed record StagedChangeSet(string StagingLocation, long RowCount)`) gains a
defaulted `bool HasChangeOrdering = false` — non-breaking to every existing construction site — set by
the staging provider when it found and staged both columns. This is how `Scd2Writer` learns whether the
staging location it's about to query has them, with no new plumbing through `RunExecutor` or the
writer's `options` bag at all.

### 3. `Scd2Writer` — the actual fix, reusing what's already correct rather than replacing it

**Design choice, made here rather than left to the plan doc's two candidates.** The plan doc weighed
capping upstream to one change per key (defeats the point — that's exactly the coalescing a
guaranteed-delivery mode exists to stop) against row-by-row processing for a duplicate key (its own
lean). A third option — a single set-based statement using `ROW_NUMBER()`/`LEAD()` window functions
over `(PARTITION BY naturalKey ORDER BY OrderingColumn)` to open every staged version in one pass — is
real, portable (window functions are ANSI SQL, identical on SQL Server and PostgreSQL, needing no new
`SqlDialect` member), and would avoid per-key round trips entirely. **Not chosen for this phase**: doing
it correctly means also detecting which staged rows are genuine *value* transitions versus a CDC event
that touched no mapped column (the existing `BuildCloseChanged` already answers this correctly for one
comparison; chaining it across a variable-length run of staged rows in one statement is a real "detect
change-points in an ordered sequence" SQL problem, and getting it exactly right needs iteration against
a live server this planning pass doesn't have. Recorded as a real future optimization, not discarded —
see "What this does not build."

**What this phase builds instead**: per-key row-by-row processing, but composed entirely from the
*existing, already-tested* single-row statement shapes — not a rewrite, and not the row-by-row-for-
everything cost the original design deliberately avoided.

```csharp
// Only when the staged batch can tell rows apart in true order — every other pairing (the
// overwhelming majority) takes the unmodified path below, unchanged.
if (staged.HasChangeOrdering)
{
    var duplicateKeys = await FindKeysWithMultipleStagedRowsAsync(targetConnection, staged, keys, cancellationToken);
    if (duplicateKeys.Count > 0)
    {
        // The bulk statements below process every OTHER key exactly as today — singleton keys are
        // the common case and pay nothing extra. Each duplicate key's own staged rows are excluded
        // from the bulk WHERE clause and processed in their own loop, in the same transaction.
        await ApplyDuplicateKeysInOrderAsync(targetConnection, transaction, staged, keys, values, duplicateKeys, cancellationToken);
    }
}

// Unchanged: BuildCloseChanged / BuildOpenVersions, now excluding duplicateKeys from their own scope
// when staged.HasChangeOrdering, and using staged.HasChangeOrdering ? ChangedAtColumn : "@now" for
// ValidFrom/ValidTo either way — a real per-row source time is strictly better than the pass time
// whenever it's available, whether or not that particular key had a duplicate.
```

`ApplyDuplicateKeysInOrderAsync`, for each duplicate key's staged rows ordered by `OrderingColumn`:
calls `HistorizedStatement.BuildCloseChanged`/`BuildOpenVersions` **against a single-row scope** — the
same statement text, parameterized to the one staged row this iteration is on (a `WHERE
{OrderingColumn} = @ordering AND {keyMatch}` added to the staging-side of each statement) — rather than
new SQL. A close that affects zero rows (the row's values don't actually differ from what's already
open — the same null-safe `differs` check already in `BuildCloseChanged`, now evaluated once per
staged row instead of once per pass) correctly skips opening a new version for that specific row, the
same way the existing single-shot statement already would. This is what makes the design correct for
"guaranteed to witness every distinct value, not merely every DML event" — a CDC update that touched no
mapped column produces no new SCD2 version, exactly as it wouldn't in a single-row pass today, just
evaluated once per staged event instead of once per pass.

The surrogate key for a row processed this way is `{OrderingColumn}|{naturalKey}`, not
`{prefix}|{naturalKey}` — `OrderingColumn` is unique per row by construction (LSN+seqval), so two
versions of one key opened in the same pass can no longer collide, which is the actual fix. The
singleton-key bulk path keeps `{prefix}|{naturalKey}`, unchanged — a single staged row per key can
never collide with itself.

### 4. `FindKeysWithMultipleStagedRowsAsync`

One query, run only when `staged.HasChangeOrdering`:

```sql
SELECT <naturalKey columns>
FROM {staging}
GROUP BY <naturalKey columns>
HAVING COUNT(*) > 1;
```

Cheap relative to the pass it belongs to — the staged set is already bounded by phase 84's row cap, and
this is one aggregate scan of it, not a per-row query.

## What this does not build

- **The window-function, fully-set-based alternative for duplicate keys.** Real, weighed, not chosen —
  see "Scd2Writer," above. A genuine future optimization once there's a live server to iterate the
  change-point detection against; the row-by-row design this phase ships is correct today and pays its
  extra cost only when a key actually has more than one staged row, which was always the plan doc's own
  bar for "acceptable."
- **A `guaranteed-delivery` mode, config toggle, or capability declaration an operator sets.**
  Resolves the plan doc's open question 4: there is no mode to turn on. `IChangeReader.CapturesChangeOrder`
  is automatic, the same way `DetectsDeletes` already is — a reader either can state true order and
  time for its rows or it can't, and `Scd2Writer` adapts to whichever is true without anyone declaring
  intent. Nothing needs warning at save time the way phase 51's delete-blind-SCD2 check does, because
  there is no wrong pairing here to warn about — every pairing just gets the best correctness its
  reader can support.
- **Ordering/timestamp support for any reader but `MsSqlCdcReader`.** Change Tracking already collapses
  to net rows (can't produce a same-pass duplicate key to begin with); Watermark and the generic readers
  have no source-side ordering concept to expose. `CapturesChangeOrder` defaults to `false` for all of
  them, unchanged.
- **Transactional-integrity-across-a-split as a new guarantee.** Unchanged from phase 84's own
  position: row-level ordering is preserved; a source transaction spanning a read boundary is still not
  guaranteed to land in one DbDataSync pass.

## Open questions — resolved

1. *"Is the same-key collision a real, reproducible defect?"* — already confirmed by the plan doc via
   code inspection; unchanged here.
2. *"Should the same-key-collision fix ship on its own, ahead of the broader guaranteed-delivery
   mode?"* — moot under this design: there is no separate "narrow fix" and "broad mode." The fix only
   ever activates when `staged.HasChangeOrdering` is true (today: CDC only), and every other pairing is
   byte-for-byte unchanged — so shipping it *is* shipping the guarantee, in one phase, not two.
3. *"Cap batches to one change per key upstream, or make the writer process duplicates in order?"* —
   the writer, as leaned — see "Scd2Writer," above, including the window-function alternative
   considered and deferred.
4. *"Exact mechanism for declaring/enforcing the guarantee?"* — `IChangeReader.CapturesChangeOrder` →
   `StagedChangeSet.HasChangeOrdering`, both automatic capability signals, no operator-facing toggle.

## How to verify when built

- **`MsSqlCdcStatementTests`** — the two new projected columns appear in both the unbounded and bounded
  SQL shapes; `NetChanges` mode still produces both (with a literal zero seqval half); ordinal math
  (`PositionOrdinal` and the two new columns' own ordinals) stays correct with the row cap on and off.
- **`MsSqlStagingTableProviderTests`/`BatchInsertStagingProviderTests`** — a `ChangeRow` stream whose
  schema includes both `ChangeOrdering` columns stages them and returns `HasChangeOrdering: true`; a
  stream without them stages exactly as before with `HasChangeOrdering: false`.
- **`Scd2WriterTests`** (unit, statement shape) — the duplicate-key exclusion clause and the
  single-row-scoped close/open statements, asserted as text, the same way `KeyReconcileScd2CloseStatement`
  was tested without a server.
- **A real reproduction, integration** (`Category=Integration`, real SQL Server, CDC enabled): two
  updates to one key between reads — the exact scenario the plan doc confirmed crashes today — now
  produces two ordered SCD2 versions with no PK violation; each version's `ValidFrom`/`ValidTo` matches
  its own change's mapped time, not the pass time; a key with three changes where the middle one
  touched no mapped column produces two versions, not three (the value-transition check firing);
  singleton-key rows in the same pass are unaffected and keep the pass-wide `prefix`-based surrogate.
- Confirm no regression: existing `Scd2NaturalKeyIntegrationTests`/`ReconcileDeletesScd2IntegrationTests`
  (phase 129) green unchanged — neither pairs `Scd2Writer` with `MsSqlCdcReader` in a duplicate-producing
  way, so `HasChangeOrdering` should never engage the new path for them, and this phase must not have
  broken that.

## References

- `architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md` — the confirmed
  defect and the two candidate fixes this phase chooses between.
- `architecture/implementation/done/phase-084-cdc-row-bounded-reads.md` — Follow-up 1, already shipped;
  this phase's row cap and `BoundedRead.PositionColumn` precedent for an out-of-band vs. schema-visible
  extra column.
- `src/DbDataSync.Drivers.Generic/Scd2Writer.cs`, `HistorizedStatement.cs` — the statements this phase
  reuses rather than replaces.
- `src/DbDataSync.Drivers.MsSql/MsSqlCdcReader.cs`, `MsSqlCdcStatement.cs`,
  `MsSqlCdcCatalog.MapLsnToTimeAsync` — the existing LSN-to-time mechanism this phase applies per row
  instead of only at the pass's ending position.
- `src/DbDataSync.Drivers.Abstractions/IStagingProvider.cs`, `StagedChangeSet.cs` — the two extension
  points (`StageAsync` already streams real `ChangeRow`s; the record already accepts an additive
  field) that make this phase's plumbing additive rather than a breaking interface change.
