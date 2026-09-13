# Phase 129 — SCD2 delete detection via `KeyReconcileScd2Close`

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/scd2-delete-detection.md` (resolved 2026-09-12), which
itself builds on `architecture/planning/done/watermark-delete-detection.md` and
`architecture/implementation/done/phase-124-key-reconcile-delete-detection.md` /
`phase-125-reconcile-config-and-scheduling.md`. Reuses `KeyReconcileReader`, `DeleteGuard`,
`RunKind.ReconcileDeletes` and the whole trigger/scheduling machinery those phases built, unchanged.
This phase adds exactly one new ending for that existing signal: closing an SCD2 version instead of
deleting a row.

## Why

`Scd2Writer.SupportsReconciliation => false` (`src/DbDataSync.Drivers.Generic/Scd2Writer.cs:52`), and
`HistorizedStatement.BuildCloseChanged` only closes a version when the staging table carries a row
with `operation = 'D'` (`src/DbDataSync.Drivers.Generic/HistorizedStatement.cs:70`) — a signal only a
delete-reporting reader (MSSQL Change Tracking/CDC) ever produces. A PostgreSQL source, or any source
on `Watermark`/a scripted change query, can never close an SCD2 version today: a row deleted at the
source just stays `IsCurrent = true` forever.

Phase 124 built the missing signal cheaply for the plain-table case
(`KeyReconcileReader` + `KeyReconcileDeleteWriter`) without needing a delete-aware reader. SCD2 was out
of scope then only because `ConfigValidation.ValidateKeyReconcilePairing` hard-rejects any writer but
`KeyReconcileDelete` paired with `KeyReconcile` — not because SCD2 was considered and rejected.

The reader already lines up for free: `KeyReconcileReader.KeyColumnMappings` stages exactly the
source's primary-key columns (`KeyReconcileReader.cs:47`), and `Scd2Writer`'s natural key, when
undeclared, is derived from that identical predicate — `NaturalKeyDerivation.Derive` also loops
`sourceColumns.Where(c => c.IsPrimaryKey)` (`NaturalKeyDerivation.cs:51`). Same predicate, same
source: the staged key set and the SCD2 business key are, by construction, the same columns whenever
nobody has stated a custom `naturalKey`.

## What this builds

### 1. A new writer — `KeyReconcileScd2CloseWriter`

New file `src/DbDataSync.Drivers.Generic/KeyReconcileScd2CloseWriter.cs`, modelled directly on
`KeyReconcileDeleteWriter.cs` (same count-then-act-then-guard shape, same one-transaction contract,
same reason for building two separate `SegmentScope` instances — one per statement, since a
`DbParameter` belongs to only one `DbCommand.Parameters` collection at a time,
`KeyReconcileDeleteWriter.cs:49-52`). Two differences from its sibling:

- The anti-join key is the **natural key**, not `shape.PrimaryKeyColumns` — an SCD2 target's real
  primary key is the generated surrogate (`HistorizedColumns.SurrogateKey`), which would match
  nothing. The natural key is read from `Scd2Writer.NaturalKeyOption` in the writer's own options bag
  — the *same* option `Scd2Writer` reads, not a new one, so a mapping's primary writer and its
  reconcile-close companion can never disagree about identity.
- The act is an `UPDATE` that closes a version, not a `DELETE`.

`Scd2Writer.SplitColumns` (`Scd2Writer.cs:165-192`) already does exactly the "read `naturalKey`,
validate it's required and mapped, split keys from values" work this writer needs for its key half —
change its access modifier from `private static` to `internal static` so
`KeyReconcileScd2CloseWriter`, in the same assembly, can call it directly and get the identical
required-option and unmapped-key error messages for free, rather than duplicating that logic. The new
writer only needs `SplitColumns(...).Keys`; it never touches `.Values` (nothing is compared —
whether to close a version here is decided entirely by presence/absence in the staged key set).

```csharp
public sealed class KeyReconcileScd2CloseWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.KeyReconcileScd2Close;
    public bool SupportsReconciliation => true;   // the reconciling half of this pair, same sense KeyReconcileDeleteWriter is.

    public async Task<WriteResult> ApplyAsync(...)
    {
        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = TargetShape.FromCachedColumns(dialect, mappingName, targetColumns, target, columnMappings);
        var (naturalKey, _) = Scd2Writer.SplitColumns(options, columnMappings, target);

        var segment = SegmentSerializer.ReadOptional(options);
        var guard = DeleteGuardOption.Read(options);
        var now = DateTimeOffset.UtcNow.UtcDateTime;

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            long scopeCount;
            using (var countCmd = targetConnection.CreateTimedCommand())
            {
                var countScope = SegmentScope.Build(dialect, binder, segment, shape.Columns, columnMappings);
                countCmd.Transaction = transaction;
                countCmd.CommandText = KeyReconcileScd2CloseStatement.BuildCount(dialect, shape.QuotedTarget, countScope.Predicate);
                countScope.AddTo(countCmd);
                scopeCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
            }

            long closed;
            using (var closeCmd = targetConnection.CreateTimedCommand())
            {
                var closeScope = SegmentScope.Build(dialect, binder, segment, shape.Columns, columnMappings);
                closeCmd.Transaction = transaction;
                closeCmd.CommandText = KeyReconcileScd2CloseStatement.BuildClose(
                    dialect, shape.QuotedTarget, closeScope.Predicate, staged.StagingLocation, naturalKey);
                closeScope.AddTo(closeCmd);
                closeCmd.AddParameter(dialect.ParameterName("now"), now);
                closed = await closeCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var result = DeleteGuardEvaluator.Check(guard, scopeCount, closed);
            if (!result.Ok) throw new InvalidOperationException(result.Message);

            await transaction.CommitAsync(cancellationToken);
            return new WriteResult(closed);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }
}
```

`DescribeAsync` mirrors `KeyReconcileDeleteWriter.DescribeAsync` — two `PreviewStatement`s ("Count the
open scope" / "Close versions absent from the staged set"), same guard-aware note text.

### 2. The statement builder — `KeyReconcileScd2CloseStatement`

New static class in the same file, mirroring `KeyReconcileDeleteStatement`
(`KeyReconcileDeleteWriter.cs:121-146`) — kept separate from the writer, same reasoning: assertable
without a server.

```csharp
public static class KeyReconcileScd2CloseStatement
{
    // Only open rows are eligible to close — a row already closed by an earlier pass was never a
    // candidate, so it must not count toward the guard's denominator.
    public static string BuildCount(SqlDialect dialect, string quotedTarget, string scopePredicate) =>
        $"SELECT COUNT(*) FROM {quotedTarget} WHERE {dialect.QuoteIdentifier(HistorizedColumns.IsCurrent)} = {dialect.TrueLiteral} AND {scopePredicate};";

    public static string BuildClose(
        SqlDialect dialect, string quotedTarget, string scopePredicate, string stagingLocation,
        IReadOnlyList<string> naturalKeyColumns)
    {
        var isCurrent = dialect.QuoteIdentifier(HistorizedColumns.IsCurrent);
        var validTo = dialect.QuoteIdentifier(HistorizedColumns.ValidTo);
        var join = string.Join(" AND ", naturalKeyColumns.Select(k =>
            $"s.{dialect.QuoteIdentifier(k)} = {quotedTarget}.{dialect.QuoteIdentifier(k)}"));

        return $"""
            UPDATE {quotedTarget}
            SET {validTo} = {dialect.ParameterReference("now")},
                {isCurrent} = {dialect.FalseLiteral}
            WHERE {isCurrent} = {dialect.TrueLiteral}
              AND {scopePredicate}
              AND NOT EXISTS (SELECT 1 FROM {stagingLocation} s WHERE {join});
            """;
    }
}
```

Same correlated `NOT EXISTS` `KeyReconcileDeleteStatement.BuildDelete` uses, for the same reason —
portable, and correct when a staged key column can be `NULL` — and the same `SET`/`WHERE` shape
`HistorizedStatement.BuildCloseChanged` already uses for an explicit-delete close
(`HistorizedStatement.cs:62-73`), so a row closed by this writer is indistinguishable afterward from
one `Scd2Writer` itself closed.

### 3. `GenericDriverKinds` — a new Kind constant

`src/DbDataSync.Drivers.Generic/GenericDriverKinds.cs`, right after `KeyReconcileDelete` (line 26):

```csharp
/// <summary>Closes the open SCD2 version of every key absent from the staged key set — never deletes
/// a row. Always paired with <see cref="KeyReconcile"/>, and only legal when the mapping's own writer
/// is <see cref="Scd2"/>.</summary>
public const string KeyReconcileScd2Close = "KeyReconcileScd2Close";
```

**Kind name, decided**: `KeyReconcileScd2Close` — matches the `KeyReconcile<Ending>` shape
`KeyReconcileDelete` already established (verb describing what the reconcile pass does to a matched
row), and is unambiguous about which writer family it closes a version for. This resolves the planning
doc's open question 1.

### 4. Engine registration — MsSql and Postgres

Both drivers register the new writer immediately after `KeyReconcileDeleteWriter`, the same place
phase 124 added that one:

- `src/DbDataSync.Drivers.MsSql/MsSqlDriver.cs`, in the `Writers` list, after line 53
  (`new KeyReconcileDeleteWriter(...)`), before the (independently, pre-existing) oddly-indented
  `new SnapshotWriter(...)` at line 54:
  `new KeyReconcileScd2CloseWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),`
- `src/DbDataSync.Drivers.Postgres/PostgresDriver.cs`, in the `Writers` list, after line 50
  (`new KeyReconcileDeleteWriter(...)`):
  `new KeyReconcileScd2CloseWriter(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),`

No new reader registration — `KeyReconcileReader` is already registered on both
(`MsSqlDriver.cs:38`, `PostgresDriver.cs:41`) and needs no change.

### 5. `RunExecutor.WithDerivedNaturalKeyAsync` — recognize the new writer Kind

`src/DbDataSync.TaskRunner/RunExecutor.cs:1434` currently gates natural-key derivation to `Scd2` only:

```csharp
if (writerKind != GenericDriverKinds.Scd2)
    return options;
```

becomes:

```csharp
if (writerKind != GenericDriverKinds.Scd2 && writerKind != GenericDriverKinds.KeyReconcileScd2Close)
    return options;
```

Same derivation, same injected `Scd2Writer.NaturalKeyOption` key, so a reconcile-close pass derives
the natural key exactly the way a primary `Scd2` pass already does when nothing is stated. No other
change to this method.

**The staging-column-filter wrinkle (traced, not a gap).** `RunExecutor.cs:683`
(`stagingColumnMappings = readerKind == GenericDriverKinds.KeyReconcile ? KeyReconcileReader.KeyColumnMappings(...) : mapping.ColumnMappings`)
is gated on **`readerKind`**, not `writerKind`. Since this phase reuses `KeyReconcileReader`
completely unchanged and pairs it with a new *writer*, this filter already produces the right staging
shape for `KeyReconcileScd2Close` with no change at all — it was never writer-aware to begin with, and
correctly so: the staging table's shape is a property of what the *reader* projected, not of what the
writer will do with it. This is the opposite of phase 124's own real gap (a `RunExecutor` fix keyed
correctly by `readerKind` from the start, not something that happened to work by luck) — confirmed by
reading the surrounding comment block (`RunExecutor.cs:675-682`), which already reasons about this in
exactly those terms ("staging has no idea 'KeyReconcile' exists, so this filters on its behalf").

`WithGuard` (`RunExecutor.cs:1471-1478`) also needs no change — it sets `DeleteGuardOption.OptionKey`
regardless of which writer is on the other end of the pipeline, exactly as phase 124's plan described.

### 6. `PipelineResolution.ReconcileWriterKind` — default depends on the mapping's own writer

`src/DbDataSync.Core/Config/PipelineResolution.cs:69-70`:

```csharp
public static string ReconcileWriterKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
    Reconcile(task, mapping).Writer?.Kind ?? "KeyReconcileDelete";
```

becomes:

```csharp
public static string ReconcileWriterKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
    Reconcile(task, mapping).Writer?.Kind
    ?? (Writer(task, mapping).Kind == "Scd2" ? "KeyReconcileScd2Close" : "KeyReconcileDelete");
```

Plain string literals, not `GenericDriverKinds` constants — `DbDataSync.Core` has no project reference
to `DbDataSync.Drivers.Generic` (confirmed: `DbDataSync.Core.csproj` has zero `ProjectReference`
entries at all), the same reason `ConfigValidation.ValidateKeyReconcilePairing`'s own doc comment
already gives for using `"KeyReconcile"`/`"KeyReconcileDelete"` literals
(`ConfigValidation.cs:213-217`). An explicit `ReconcileConfig.Writer` override still wins outright,
unchanged — this only changes what "unset" resolves to.

### 7. Validation — `ConfigValidation.cs`, and a real layering gap the planning doc missed

`ValidateKeyReconcilePairing` (`ConfigValidation.cs:219-257`) needs its writer check widened from an
equality test to a membership test, plus two Scd2-specific checks. Both new checks need to know the
mapping's **own primary writer** (Kind and Options) — a value the pairing check has never needed
before, so its signature grows by two parameters:

```csharp
public static void ValidateKeyReconcilePairing(
    string readerKind, string writerKind, TableMappingConfig mapping,
    string primaryWriterKind, IReadOnlyDictionary<string, string> primaryWriterOptions)
{
    const string keyReconcileReader = "KeyReconcile";
    const string keyReconcileDeleteWriter = "KeyReconcileDelete";
    const string keyReconcileScd2CloseWriter = "KeyReconcileScd2Close";

    var readerIsKeyReconcile = readerKind == keyReconcileReader;
    var writerIsValidPair = writerKind == keyReconcileDeleteWriter || writerKind == keyReconcileScd2CloseWriter;

    if (readerIsKeyReconcile != writerIsValidPair)
        throw new ConfigValidationException(
            $"Table mapping '{mapping.Name}' pairs reader '{readerKind}' with writer '{writerKind}'. " +
            $"'{keyReconcileReader}' must always be paired with '{keyReconcileDeleteWriter}' or " +
            $"'{keyReconcileScd2CloseWriter}' — any other combination would re-insert or corrupt rows " +
            "a delete-diff sweep only ever means to remove or close.");

    if (!readerIsKeyReconcile) return;

    // ... existing "no cached source columns" / "no primary key" / "unmapped key column(s)" checks,
    // unchanged (ConfigValidation.cs:236-256) ...

    if (writerKind != keyReconcileScd2CloseWriter) return;

    if (primaryWriterKind != "Scd2")
        throw new ConfigValidationException(
            $"Table mapping '{mapping.Name}' pairs '{keyReconcileScd2CloseWriter}' with a primary " +
            $"writer of '{primaryWriterKind}', not 'Scd2'. Closing a version on a target that isn't " +
            "versioned is meaningless — this writer only pairs with a mapping whose own writer is Scd2.");

    if (primaryWriterOptions.TryGetValue("naturalKey", out var stated) && !string.IsNullOrWhiteSpace(stated))
    {
        var statedKeys = stated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var derivedTargetNames = keyColumns   // already computed above, from mapping.SourceColumns.Where(c => c.IsPrimaryKey)
            .Select(c => mapping.ColumnMappings.First(
                m => string.Equals(m.SourceColumn, c.Name, StringComparison.OrdinalIgnoreCase)).TargetColumn)
            .ToList();

        if (!statedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(derivedTargetNames.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new ConfigValidationException(
                $"Table mapping '{mapping.Name}' states an Scd2 natural key ({string.Join(", ", statedKeys)}) " +
                $"that differs from the source's primary key ({string.Join(", ", derivedTargetNames)}) — the " +
                $"only columns '{keyReconcileReader}' actually stages. '{keyReconcileScd2CloseWriter}' would " +
                "join on columns the staging table doesn't have. Either drop the custom natural key, or keep " +
                "this mapping on the status quo (no SCD2 delete detection) until a future phase lets " +
                $"'{keyReconcileReader}' stage an arbitrary column list.");
    }
}
```

**A real gap in the planning doc, found and corrected here.** The plan's own *Validation* section says
to check the stated key by comparing it against
`NaturalKeyDerivation.Derive(mapping.SourceColumns, mapping.ColumnMappings)` directly. That does not
compile as written: `NaturalKeyDerivation` lives in `DbDataSync.Drivers.Generic`, and
`DbDataSync.Core` (where `ConfigValidation` lives) has **no project reference to it at all** — Core
sits below the driver layer by design, which is exactly why `ConfigValidation.cs:213-217`'s own
existing doc comment already explains using plain `"KeyReconcile"`/`"KeyReconcileDelete"` string
literals instead of `GenericDriverKinds` constants ("Core cannot reference the driver layer, which is
what keeps config depending on drivers and not the other way round"). `Drivers.Generic` in turn already
references `Core` (for `ColumnMapping`, `TableMappingConfig`, etc.), so the reverse reference the plan
implicitly asks for would be circular. Separately, `NaturalKeyDerivation.Derive` also takes
`IReadOnlyList<ColumnMetadata>` (`DbDataSync.Drivers.Abstractions`), while `mapping.SourceColumns` is
`IReadOnlyList<CachedColumn>` (`DbDataSync.Core.Config`) — a structurally identical but distinct type,
so even a same-layer call would need a conversion. The fix above sidesteps both problems by
duplicating the tiny derivation predicate inline (source primary-key columns, translated through
`ColumnMappings` to target-column names) over the `CachedColumn`/`ColumnMapping` types
`ValidateKeyReconcilePairing` already has in scope — the same predicate
`NaturalKeyDerivation.Derive` and `KeyReconcileReader.KeyColumnMappings` both use
(`c.IsPrimaryKey`), just computed a second time rather than called cross-layer, matching this file's
existing precedent of duplicating rather than importing across the Core/driver boundary.

**Where the new checks live, decided**: inside `ValidateKeyReconcilePairing` itself, as an added
branch — not a new sibling function. This resolves the planning doc's open question 3. Reasoning: the
function is called from two places (`ConfigRepository.SaveTableMapping` directly, for the ordinary
Change-Processing pipeline, and again inside `ValidateReconcile` for the `ReconcileConfig` pipeline —
`ConfigRepository.cs:263-268`), and **both** already compute `PipelineResolution.Writer(task,
mapping)` for other purposes at that call site (`ConfigRepository.cs:257`, for
`ValidateHistorizedTarget`) — so both call sites can supply the two new parameters with no new lookup.
A separate `ValidateScd2ReconcilePairing` sibling would need its own call added at both sites (one of
which is easy to forget, the same "who remembers to call the second validator" risk this repo's own
`ValidateReconcile` doc comment already flags as the reason it reuses `ValidateKeyReconcilePairing`
"verbatim" rather than re-implementing the check). Extending the one function keeps "is this pairing
legal" a single decision point.

`ValidateReconcile` (`ConfigValidation.cs:268-285`) needs its own signature widened to receive and
forward the same two new parameters to its internal call at line 274 — no other change; its
cadence/after-change rules stay writer-agnostic exactly as its own doc comment already says.

`ConfigRepository.SaveTableMapping` (`ConfigRepository.cs:241-268`) updates both call sites:

```csharp
var primaryWriter = PipelineResolution.Writer(task, mapping);   // already computed, reused
...
ConfigValidation.ValidateKeyReconcilePairing(
    PipelineResolution.Reader(task, mapping).Kind, PipelineResolution.Writer(task, mapping).Kind, mapping,
    primaryWriter.Kind, primaryWriter.Options);

ConfigValidation.ValidateReconcile(
    PipelineResolution.Reconcile(task, mapping), mapping,
    PipelineResolution.ReconcileReaderKind(task, mapping), PipelineResolution.ReconcileWriterKind(task, mapping),
    primaryWriter.Kind, primaryWriter.Options);
```

### 8. SPA

- `types.ts:1007` — `RECONCILE_ONLY_KINDS` gains `'KeyReconcileScd2Close'`, excluded from the ordinary
  Change Processing pickers the same way its siblings already are.
- **A second, real correction to the planning doc.** It states: *"`ReconcileConfigCard` needs no new
  field — the writer picker it already has (phase 125) just gains a new option, and the default-writer
  text can say which one a mapping will actually get."* Reading
  `src/DbDataSync.Web/src/pages/replication-detail/ReconcileConfigCard.tsx` in full: **there is no
  writer picker in this component at all.** It renders a fixed, static hint string at lines 139–142:
  `Always the `KeyReconcile`/`KeyReconcileDelete` pair …` — this matches phase 125's own retrospective
  note that "no mapping-level override editor in the SPA yet" exists. Once `ReconcileWriterKind`'s
  default depends on the mapping's own writer (§6 above), that hardcoded sentence becomes **factually
  wrong** for any mapping whose primary writer is `Scd2`. The fix: make the hint conditional on the
  mapping's own writer kind, which `OverviewPanel.tsx` (the only caller, line 298) already has in scope
  as `draft.changeProcessing.writer.kind` — pass it down as a new prop (e.g. `writerKind: string`) and
  render `KeyReconcile`/`KeyReconcileScd2Close` when it is `'Scd2'`, `KeyReconcile`/`KeyReconcileDelete`
  otherwise — mirroring `PipelineResolution.ReconcileWriterKind`'s own default logic client-side. This
  is a small, real, required change; it is not "no new field" as the plan claimed, though it is also
  not the writer-picker-gains-an-option change the plan described (no such picker exists to extend).
- **Open question, resolved**: whether "Reconcile deletes" copy changes. **Decision: leave the button
  label, the hook name, and the request-type name exactly as they are** —
  `ReplicationDetailPage.tsx:179` ("Reconcile deletes…"), `useReconcileDeletes`,
  `ReconcileDeletesRequest` (`types.ts:994`). The concept — "reconcile what the source no longer has" —
  stays honest even though the literal word "delete" is inexact for an `Scd2` mapping, and renaming
  touches a request type, a hook, a form and a button across the SPA plus
  `RunsController`/`ReconcileService` for a wording-only reason; the run's own log lines and
  `RunKindBadge` already say what actually happened, matching this repo's own precedent (`RunKind`
  stays `ReconcileDeletes` for the identical reason — see §9). **However**, one specific sentence
  crosses from "loose terminology" into "factually wrong": `ReconcileDeletesForm.tsx:124-126`'s hint
  — *"removes target rows whose key is no longer there... Never inserts or updates"* — is not true for
  an `Scd2`-resolved mapping, which closes a version rather than removing a row. This sentence should
  become conditional on the selected mapping's resolved writer (the form already has
  `selectedMapping` and the mapping list with capabilities in scope), saying "closes the version of"
  rather than "removes" when the resolved writer is `KeyReconcileScd2Close`. This is a factual-copy fix
  distinct from the naming question, and is in scope for this phase since it's directly caused by this
  phase's own new writer existing.

### 9. No new `RunKind`, no new reader

Unchanged from the plan and worth restating precisely, since both are easy to second-guess once a new
writer exists: `RunKind.ReconcileDeletes` is reused as-is (the concept is identical; only the writer's
ending differs, and the writer `Kind` on the run/log already carries that distinction) —
`RunExecutor` and `RunLanes` need no change here, beyond §5's one-line gate. `KeyReconcileReader` is
reused exactly as phase 124 built it, registered nowhere new.

### 10. Test changes

- **`tests/DbDataSync.Drivers.Generic.Tests/KeyReconcileStatementTests.cs`** — add a
  `KeyReconcileScd2CloseStatementTests` class (same file or a sibling file) mirroring the existing
  `Count_ScopesToTheSegment` / `Delete_UsesACorrelatedNotExists...` /
  `Delete_WithACompositeKey_JoinsOnEveryKeyColumn` / `Delete_FollowsTheDialectForQuoting` cases, against
  `KeyReconcileScd2CloseStatement.BuildCount`/`BuildClose`: assert the generated `UPDATE` sets
  `DS_ValidTo`/`DS_IsCurrent`, the count includes `DS_IsCurrent = 1`, the `NOT EXISTS` join uses the
  *natural* key columns passed in (not the target's primary key), a composite natural key joins on
  every column, and dialect quoting is respected (`BracketDialect`/`ColonDialect`, same as the existing
  tests).
- **`tests/DbDataSync.Core.Tests/KeyReconcilePairingValidationTests.cs`** — every existing case needs
  its `ValidateKeyReconcilePairing` calls updated for the two new parameters (pass `"MsSqlMerge"`/`{}`
  or similarly inert values where the primary writer doesn't matter to that case). New cases:
  `KeyReconcile` + `KeyReconcileScd2Close` + primary writer `Scd2` + no stated key → passes;
  + primary writer `MsSqlMerge` → rejected, message names `Scd2`; + a stated key equal to the derived
  one → passes; + a stated key naming a different column → rejected, message names both key sets.
- **`tests/DbDataSync.Core.Tests/ReconcileValidationTests.cs`** — mirror the same new cases through
  `ValidateReconcile`, plus its existing cases' calls updated for the widened signature.
- **`tests/DbDataSync.TaskRunner.Tests/Scd2NaturalKeyIntegrationTests.cs`** — the natural home for an
  end-to-end test (it already provisions a live `Scd2` target with real `DS_IsCurrent`/`DS_ValidTo`
  columns; `RunExecutorIntegrationTests`'s own phase-124 fixture is on a non-historized writer and
  would need a whole new target shape to reuse). Add a `ReconcileDeletes` helper mirroring
  `RunExecutorIntegrationTests.EnqueueAndDrainReconcileAsync`
  (`RunExecutorIntegrationTests.cs:1061-1070`), enqueuing
  `new WorkItemKinds(GenericDriverKinds.KeyReconcile, GenericDriverKinds.StagingTable, GenericDriverKinds.KeyReconcileScd2Close)`.
  New facts: deleting an order at the source and sweeping closes exactly that key's open version
  (`IsCurrent` false, `ValidTo` set) while an untouched key's version stays open and an
  updated-not-deleted key's version is unaffected by the sweep (only a Primary pass would close it, for
  its own reason); a segmented sweep only closes within its own range, mirroring
  `ReconcileDeletes_ASegmentedSweep_OnlyDeletesWithinItsOwnRange`
  (`RunExecutorIntegrationTests.cs:1106-1124`); exceeding the default guard rolls the whole `UPDATE`
  back (no version closes), mirroring `ReconcileDeletes_ExceedingTheDefaultGuardsRatio...`
  (`RunExecutorIntegrationTests.cs:1131-1149`); an override guard closes everything, mirroring
  `ReconcileDeletes_WithAnOverrideGuard...` (`RunExecutorIntegrationTests.cs:1154-1167`).
- **`tests/DbDataSync.Api.Tests/ReconcileDeletesIntegrationTests.cs`** — add one end-to-end case (or a
  sibling test class, same shape as this file's `IClassFixture<TestApiFactory>` setup) with the
  mapping's `ChangeProcessing.Writer.Kind = "Scd2"` and `Reconcile.Writer.Kind =
  "KeyReconcileScd2Close"`, confirming the real HTTP trigger closes a version rather than deleting a
  row, and that `GET .../runs?kind=ReconcileDeletes` still reports it the same way phase 124's plain
  case does (`ReconcileDeletesIntegrationTests.cs:83-102`).
- **`DeleteGuardEvaluatorTests.cs`** — no change needed. `DeleteGuardEvaluator.Check` is pure
  row-count arithmetic, already writer-agnostic, and this writer reuses it verbatim with `closed` in
  the same argument position `deleted` occupies for the delete case.

## What this phase does not do

- **Does not touch `Snapshot`.** `SnapshotWriter.SupportsReconciliation => false` for a real reason
  (`SnapshotWriter.cs:20-21`) — no `IsCurrent` concept, nothing to close.
- **Does not add a new `RunKind`** — see §9.
- **Does not add a new reader** — `KeyReconcileReader` is reused exactly as phase 124 built it.
- **Does not support a stated `naturalKey` that isn't the source's primary key.** A mapping that
  genuinely needs a business key different from its technical primary key keeps today's status quo (no
  SCD2 delete detection without a delete-reporting reader) until a future phase, if ever, teaches
  `KeyReconcileReader` to stage an arbitrary column list instead of `IsPrimaryKey`.
- **Does not change what "deletes need a reader that reports them" means for a source that already has
  one.** MSSQL's Change Tracking/CDC readers keep working exactly as today; this is purely a second
  path for sources with no such reader.
- **Does not rename any SPA copy, request type, hook, or controller** beyond the one factual-accuracy
  fix in §8.

## How to verify when built

- `dotnet test tests/DbDataSync.Drivers.Generic.Tests` — the new `KeyReconcileScd2CloseStatementTests`
  green, with no server.
- `dotnet test tests/DbDataSync.Core.Tests` — `KeyReconcilePairingValidationTests` and
  `ReconcileValidationTests`' new and updated cases green, with no server.
- `dotnet test tests/DbDataSync.TaskRunner.Tests --filter Category=Integration` against the Docker SQL
  Server container — the extended `Scd2NaturalKeyIntegrationTests` green, including the guard and
  segmentation cases.
- `dotnet test tests/DbDataSync.Api.Tests --filter Category=Integration` — the new
  `ReconcileDeletesIntegrationTests` (or sibling) case green, end to end through real HTTP.
- Manual: a Postgres source mapping configured with `Scd2` (no delete-aware reader exists for Postgres
  at all), reconcile enabled with `KeyReconcileScd2Close` — delete a source row, trigger
  `POST .../reconcile-deletes`, confirm the target's matching version has `DS_IsCurrent = false` and a
  populated `DS_ValidTo`, and that an unrelated row's open version is untouched.
- Confirm `dotnet build` succeeds — specifically confirms the `ConfigValidation` fix in §7 doesn't
  introduce the circular `Core → Drivers.Generic` reference the planning doc's original wording would
  have required.

## Open questions

None left genuinely open after this session's research — the three the planning doc raised are
resolved in §3 (Kind name), §8 (SPA copy), and §7 (validation placement), each with reasoning grounded
in the actual code read for this phase, not merely picked between two equally defensible options.

The one thing worth flagging rather than silently deciding: `Scd2Writer.SplitColumns`'s visibility
change (`private` → `internal`, §1) is a small, real `.cs` change to an *existing, shipped* writer this
phase's whole design depends on for correctness (reusing its exact required/unmapped-key checks) — it
carries no behavior change (still not visible outside `DbDataSync.Drivers.Generic`), but it is worth
the implementer double-checking `Scd2Writer`'s existing tests (`Scd2NaturalKeyIntegrationTests.cs`)
still pass unmodified after the visibility change, since nothing about this phase's own test plan
exercises `Scd2Writer` directly.
