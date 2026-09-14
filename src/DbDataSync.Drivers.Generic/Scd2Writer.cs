using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Slowly Changing Dimension Type 2: keeps every version of every key, rather than the current one.
///
/// <para>
/// Three set-based statements in one transaction — close what changed, open what has no open version,
/// and nothing at all for a key whose values are the same. Row by row would be correct and would take
/// a pass proportional to the staged set rather than to what actually changed.
/// </para>
///
/// <para>
/// **A key with no open version is what "insert" means here**, which is why closing runs first: after
/// it, "no open version" is exactly the new keys plus the ones this pass just closed, and the second
/// statement needs no notion of which is which.
/// </para>
///
/// <para>
/// **Deletes need a reader that reports them.** Paired with one that does not, a key that disappears
/// at the source stays current here forever — allowed, not refused, because a delete-blind reader with
/// SCD2 is a real configuration for a source that never deletes. The writer picker says so where the
/// choice is made rather than leaving it to be discovered.
/// </para>
///
/// <para>
/// Not reconciling: a full "make the target match" pass is meaningless for a writer whose job is
/// preserving what used to be there.
/// </para>
/// </summary>
public sealed class Scd2Writer(SqlDialect dialect, ITableCatalog catalog) : IChangeWriter, IStatementPreview
{
    /// <summary>
    /// Which mapped columns identify a row across its versions — the dimension's business key.
    /// <para>
    /// Not inferable from the *target*: in an SCD2 table the primary key is the surrogate this writer
    /// generates, so there is nothing there to read a business key from. It is inferable from the
    /// **source's** primary key, translated through the mapping's columns, which is what
    /// <see cref="NaturalKeyDerivation"/> does and what <c>RunExecutor</c> injects here when this
    /// option is absent (phase 68). Stating it stays possible, per mapping, because which columns make
    /// a customer the same customer is ultimately a modelling decision rather than a schema fact.
    /// </para>
    /// </summary>
    public const string NaturalKeyOption = "naturalKey";

    public string Kind => GenericDriverKinds.Scd2;

    public bool SupportsReconciliation => false;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = NaturalKeyOption,
            Label = "Natural key",
            Description =
                "The mapped column(s) that identify a row across its versions — the business key. " +
                "Comma-separated for a composite one. Not the surrogate key, which this writer " +
                "generates. Left empty it is derived from the source table's primary key.",
            // Optional at every level since phase 68: absent means "derive it from the source's
            // primary key", which is the normal case, and the replication level cannot state one at
            // all. A run that can neither derive nor read a value still fails — in ApplyAsync below,
            // where the message can say which table had no primary key.
            Required = false,
        },
    ];

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = TargetShape.FromCachedColumns(dialect, mappingName, targetColumns, target, columnMappings);
        var (keys, values) = SplitColumns(options, columnMappings, target);

        var now = DateTimeOffset.UtcNow.UtcDateTime;
        // One prefix per pass, so two versions of one key opened by the same pass cannot collide and
        // two opened by different passes cannot either. Still what every singleton-key row uses — the
        // only keys phase 132's duplicate handling below ever touches are the ones this prefix cannot
        // protect: more than one staged row for the same key, in the same pass, which would compute the
        // identical surrogate key here and collide on the target's own primary key.
        var prefix = $"{now:yyyyMMddHHmmssfff}-";

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Only when the staged batch can tell rows apart in true order — every other pairing (the
            // overwhelming majority) never even issues the extra query below, let alone the per-row loop.
            List<object?[]> duplicateKeys = [];
            long openedFromDuplicates = 0;
            if (staged.HasChangeOrdering)
            {
                duplicateKeys = await FindKeysWithMultipleStagedRowsAsync(
                    targetConnection, transaction, staged, keys, cancellationToken);
                if (duplicateKeys.Count > 0)
                    openedFromDuplicates = await ApplyDuplicateKeysInOrderAsync(
                        targetConnection, transaction, shape, staged, keys, values, columnMappings,
                        duplicateKeys, cancellationToken);
            }

            // The bulk statements: every key but the ones just processed one row at a time, exactly as
            // before this phase — singleton keys, the common case, pay nothing extra. A real per-row
            // source time is used for ValidFrom/ValidTo instead of the pass-wide @now whenever the batch
            // can state one, whether or not that particular key had a duplicate: it is strictly better
            // information when it is available.
            var duplicateExclusion = duplicateKeys.Count > 0
                ? HistorizedStatement.BuildDuplicateKeyExclusion(dialect, staged.StagingLocation, keys)
                : null;
            var changedAtColumn = staged.HasChangeOrdering
                ? $"s.{dialect.QuoteIdentifier(ChangeOrdering.ChangedAtColumn)}"
                : null;

            using (var close = targetConnection.CreateTimedCommand())
            {
                close.Transaction = transaction;
                close.CommandText = HistorizedStatement.BuildCloseChanged(
                    dialect, shape.QuotedTarget, staged.StagingLocation, keys, values,
                    validToExpression: changedAtColumn, stagingFilter: duplicateExclusion);
                close.AddParameter(dialect.ParameterName("now"), now);
                await close.ExecuteNonQueryAsync(cancellationToken);
            }

            long opened;
            using (var open = targetConnection.CreateTimedCommand())
            {
                open.Transaction = transaction;
                open.CommandText = HistorizedStatement.BuildOpenVersions(
                    dialect, shape.QuotedTarget, staged.StagingLocation, keys, columnMappings,
                    validFromExpression: changedAtColumn, stagingFilter: duplicateExclusion);
                open.AddParameter(dialect.ParameterName("now"), now);
                open.AddParameter(dialect.ParameterName("versionKeyPrefix"), prefix);
                opened = await open.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            // Versions opened. A closed version is not a row written — it is a row amended — and
            // counting both would report a single changed key as two.
            return new WriteResult(opened + openedFromDuplicates);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The natural keys with more than one row staged this pass — see
    /// <see cref="HistorizedStatement.BuildFindDuplicateKeys"/>. Read back as actual values, not merely
    /// counted, because <see cref="ApplyDuplicateKeysInOrderAsync"/> needs them to look up each key's
    /// own staged rows afterwards.
    /// </summary>
    private async Task<List<object?[]>> FindKeysWithMultipleStagedRowsAsync(
        DbConnection targetConnection,
        DbTransaction transaction,
        StagedChangeSet staged,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        using var cmd = targetConnection.CreateTimedCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = HistorizedStatement.BuildFindDuplicateKeys(dialect, staged.StagingLocation, keys);

        var result = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[keys.Count];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Each duplicate key's own staged rows, one at a time, in true source order — reusing
    /// <see cref="HistorizedStatement.BuildCloseChanged"/>/<see cref="HistorizedStatement.BuildOpenVersions"/>
    /// scoped to a single staged row rather than any new SQL.
    /// <para>
    /// The surrogate key for a row processed here is <c>{OrderingColumn}|{naturalKey}</c>, not
    /// <c>{prefix}|{naturalKey}</c>: <see cref="ChangeOrdering.OrderingColumn"/> is unique per row by
    /// construction (LSN and seqval), so two versions of one key opened in the same pass can no longer
    /// compute the same value — the actual fix a duplicate key needs. <see cref="ChangeOrdering.ChangedAtColumn"/>
    /// is used for <c>ValidFrom</c>/<c>ValidTo</c> instead of the pass time, for the same reason every
    /// other row in this pass gets it when available: a real per-row source time beats the pass time.
    /// </para>
    /// <para>
    /// Scoped by <c>s.{OrderingColumn} = @ordering</c> alone — the ordering value is unique across the
    /// *whole* staged batch, not merely within this key, so it identifies exactly one staged row without
    /// needing the key's own values re-bound into the statement.
    /// </para>
    /// </summary>
    private async Task<long> ApplyDuplicateKeysInOrderAsync(
        DbConnection targetConnection,
        DbTransaction transaction,
        TargetShape shape,
        StagedChangeSet staged,
        IReadOnlyList<string> keys,
        IReadOnlyList<string> values,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyList<object?[]> duplicateKeys,
        CancellationToken cancellationToken)
    {
        var quotedOrdering = dialect.QuoteIdentifier(ChangeOrdering.OrderingColumn);
        var quotedChangedAt = $"s.{dialect.QuoteIdentifier(ChangeOrdering.ChangedAtColumn)}";
        var versionKeyPrefix = dialect.Concat([$"s.{quotedOrdering}", "'|'"]);

        long opened = 0;
        foreach (var keyValues in duplicateKeys)
        {
            var orderings = new List<string>();
            using (var orderingsCmd = targetConnection.CreateTimedCommand())
            {
                orderingsCmd.Transaction = transaction;
                orderingsCmd.CommandText = HistorizedStatement.BuildStagedOrderingsForKey(dialect, staged.StagingLocation, keys);
                for (var i = 0; i < keys.Count; i++)
                    orderingsCmd.AddParameter(dialect.ParameterName($"key{i}"), keyValues[i]);

                await using var reader = await orderingsCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    orderings.Add(reader.GetString(0));
            }

            foreach (var ordering in orderings)
            {
                var stagingFilter = $"s.{quotedOrdering} = {dialect.ParameterReference("ordering")}";

                using (var close = targetConnection.CreateTimedCommand())
                {
                    close.Transaction = transaction;
                    close.CommandText = HistorizedStatement.BuildCloseChanged(
                        dialect, shape.QuotedTarget, staged.StagingLocation, keys, values,
                        validToExpression: quotedChangedAt, stagingFilter: stagingFilter);
                    close.AddParameter(dialect.ParameterName("ordering"), ordering);
                    await close.ExecuteNonQueryAsync(cancellationToken);
                }

                using (var open = targetConnection.CreateTimedCommand())
                {
                    open.Transaction = transaction;
                    open.CommandText = HistorizedStatement.BuildOpenVersions(
                        dialect, shape.QuotedTarget, staged.StagingLocation, keys, columnMappings,
                        versionKeyPrefixExpression: versionKeyPrefix, validFromExpression: quotedChangedAt,
                        stagingFilter: stagingFilter);
                    open.AddParameter(dialect.ParameterName("ordering"), ordering);
                    opened += await open.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        return opened;
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var (keys, values) = SplitColumns(request.Options, request.ColumnMappings, request.Target);

        return
        [
            new PreviewStatement(
                PreviewStages.Write,
                "Close the version of every key whose values changed, or whose source row was deleted",
                HistorizedStatement.BuildCloseChanged(dialect, shape.QuotedTarget, "<staging>", keys, values),
                PreviewOrigin.BuiltIn,
                "Compared column by column and null-safely: a value becoming null is a change, and a " +
                "plain <> would call it unknown and never close the version."),

            new PreviewStatement(
                PreviewStages.Write,
                "Open a version for every key that now has none",
                HistorizedStatement.BuildOpenVersions(dialect, shape.QuotedTarget, "<staging>", keys, request.ColumnMappings),
                PreviewOrigin.BuiltIn,
                "After the close, that is the new keys and the ones just closed. A key whose values " +
                "are unchanged has an open version still and is written nothing at all."),
        ];
    }

    /// <summary>
    /// The natural key, and everything else.
    /// <para>
    /// Read from the configured setting rather than from the target's primary key, which in an SCD2
    /// table is the surrogate — there is nothing in the schema to infer a business key from, which is
    /// why it is declared.
    /// </para>
    /// <para>
    /// <c>internal</c> rather than <c>private</c> since phase 129: <see cref="KeyReconcileScd2CloseWriter"/>,
    /// in the same assembly, calls this directly for its own key half rather than duplicating the
    /// required-option and unmapped-key checks. No behavior change — still invisible outside this
    /// assembly.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<string> Keys, IReadOnlyList<string> Values) SplitColumns(
        IReadOnlyDictionary<string, string> options,
        IReadOnlyList<ColumnMapping> columnMappings,
        TableRef target)
    {
        var mapped = columnMappings.Select(m => m.TargetColumn).ToList();

        if (!options.TryGetValue(NaturalKeyOption, out var configured) || string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"The SCD Type 2 writer needs a '{NaturalKeyOption}': the column(s) that identify a row " +
                "across its versions. None was derived — the source table has no primary key, or its " +
                "key columns are not all mapped — and this target's own primary key is the generated " +
                $"{HistorizedColumns.SurrogateKey}, which says nothing about identity. Set it on this " +
                "table mapping's Pipeline tab.");

        var keys = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var unmapped = keys.Where(k => !mapped.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unmapped.Count > 0)
            throw new InvalidOperationException(
                $"The natural key names {string.Join(", ", unmapped.Select(u => $"'{u}'"))}, which " +
                $"'{target.Schema}.{target.Table}' has no mapping for. A key column has to be one this " +
                "mapping writes, or there is nothing to match versions on.");

        return (keys, mapped.Where(m => !keys.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList());
    }
}
