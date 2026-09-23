using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Closes the open SCD2 version of every key within a segment's scope that is absent from the staged
/// key set — never deletes a row. Phase 129's second ending for phase 124's delete-diff sweep: paired
/// with <see cref="KeyReconcileReader"/>, exactly like <see cref="KeyReconcileDeleteWriter"/>, but for a
/// mapping whose own writer is <see cref="Scd2Writer"/> rather than one that deletes rows outright.
/// <para>
/// The anti-join key is the **natural key**, not the target's real primary key — an SCD2 table's
/// primary key is the generated surrogate (<see cref="HistorizedColumns.SurrogateKey"/>), which would
/// match nothing in a staging table that only ever carries the source's natural key columns. Read via
/// <see cref="Scd2Writer.SplitColumns"/> — the same option, the same required/unmapped-key checks
/// <see cref="Scd2Writer"/> itself uses, so a mapping's primary writer and its reconcile-close companion
/// can never disagree about identity.
/// </para>
/// <para>
/// Same count-then-act-then-guard shape as <see cref="KeyReconcileDeleteWriter"/>, for the same reason:
/// the count establishes the guard's denominator before anything closes, both inside one transaction, and
/// two separate <see cref="SegmentScope"/> instances are built because a <see cref="DbParameter"/> can
/// belong to only one <see cref="DbCommand.Parameters"/> collection at a time.
/// </para>
/// </summary>
public sealed class KeyReconcileScd2CloseWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.KeyReconcileScd2Close;

    // The reconciling half of this pair, same sense KeyReconcileDeleteWriter is.
    public bool SupportsReconciliation => true;

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
        var (naturalKey, _) = Scd2Writer.SplitColumns(options, columnMappings, target);

        var segment = SegmentSerializer.ReadOptional(options);
        var guard = DeleteGuardOption.Read(options);
        var now = DateTimeOffset.UtcNow.UtcDateTime;

        // A fresh SegmentScope per statement, deliberately — see KeyReconcileDeleteWriter's own note:
        // a DbParameter belongs to only one DbCommand.Parameters collection at a time.
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
            if (!result.Ok)
                throw new InvalidOperationException(result.Message);

            await transaction.CommitAsync(cancellationToken);
            return new WriteResult(closed);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var scope = SegmentScope.Build(dialect, binder, segment, shape.Columns, request.ColumnMappings);
        var guard = DeleteGuardOption.Read(request.Options);
        var (naturalKey, _) = Scd2Writer.SplitColumns(request.Options, request.ColumnMappings, request.Target);

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Count the open scope",
                KeyReconcileScd2CloseStatement.BuildCount(dialect, shape.QuotedTarget, scope.Predicate), PreviewOrigin.BuiltIn,
                "Establishes the denominator the delete guard compares against, before any version closes."),

            new PreviewStatement(
                PreviewStages.Write, "Close versions absent from the staged set",
                KeyReconcileScd2CloseStatement.BuildClose(
                    dialect, shape.QuotedTarget, scope.Predicate, "<staging>", naturalKey),
                PreviewOrigin.BuiltIn,
                guard is RatioDeleteGuard ratio
                    ? $"Guarded: rolled back if this would close more than {ratio.MaxRatio:P0} of the open scope's rows."
                    : "Unguarded — every open version in scope whose key is absent from the staged set is closed."),
        ];
    }
}

/// <summary>Statement text for <see cref="KeyReconcileScd2CloseWriter"/>, separated so it can be
/// asserted without a live server — same reasoning as <see cref="KeyReconcileDeleteStatement"/>.</summary>
public static class KeyReconcileScd2CloseStatement
{
    // Only open rows are eligible to close — a row already closed by an earlier pass was never a
    // candidate, so it must not count toward the guard's denominator.
    public static string BuildCount(SqlDialect dialect, string quotedTarget, string scopePredicate) =>
        $"SELECT COUNT(*) FROM {quotedTarget} WHERE {dialect.QuoteIdentifier(HistorizedColumns.IsCurrent)} = {dialect.TrueLiteral} AND {scopePredicate}";

    /// <summary>
    /// The same correlated <c>NOT EXISTS</c> <see cref="KeyReconcileDeleteStatement.BuildDelete"/> uses,
    /// for the same reason — portable, and correct when a staged key column can be NULL — and the same
    /// <c>SET</c>/<c>WHERE</c> shape <see cref="HistorizedStatement.BuildCloseChanged"/> already uses for
    /// an explicit-delete close, so a row closed by this writer is indistinguishable afterward from one
    /// <see cref="Scd2Writer"/> itself closed.
    /// </summary>
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
              AND NOT EXISTS (SELECT 1 FROM {stagingLocation} s WHERE {join})
            """;
    }
}
