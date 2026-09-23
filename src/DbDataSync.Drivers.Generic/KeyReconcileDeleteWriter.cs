using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Deletes target rows within a segment's scope whose key is absent from the staged key set — never
/// inserts or updates. The writer half of phase 124's delete-diff sweep: paired with
/// <see cref="KeyReconcileReader"/>, which stages only the source's current primary-key values, so
/// this writer's anti-join is exactly "every target row in scope this scan didn't see".
/// <para>
/// A count-then-delete shape, both inside one transaction: the count establishes how many rows are in
/// scope before anything is removed, so <see cref="DeleteGuardEvaluator"/> can compare the delete
/// against a real denominator rather than one measured after the fact. A guard violation rolls the
/// whole transaction back — nothing is removed, the same way <see cref="DeleteInsertWriter"/>'s own
/// transaction leaves a failed apply with the target untouched.
/// </para>
/// </summary>
public sealed class KeyReconcileDeleteWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.KeyReconcileDelete;

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
        if (shape.PrimaryKeyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}' target has no cached primary key — the KeyReconcileDelete " +
                "writer needs one to anti-join staged keys against. Use Refresh metadata on this mapping.");

        var segment = SegmentSerializer.ReadOptional(options);
        var guard = DeleteGuardOption.Read(options);

        // A fresh SegmentScope per statement, deliberately: a DbParameter can belong to only one
        // DbCommand.Parameters collection at a time, and this is the first generic writer that scopes
        // two separate statements (count, then delete) to the same segment inside one transaction —
        // DeleteInsertWriter's own single DELETE never had to share parameters across commands.
        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            long scopeCount;
            using (var countCmd = targetConnection.CreateTimedCommand())
            {
                var countScope = SegmentScope.Build(dialect, binder, segment, shape.Columns, columnMappings);
                countCmd.Transaction = transaction;
                countCmd.CommandText = KeyReconcileDeleteStatement.BuildCount(shape.QuotedTarget, countScope.Predicate);
                countScope.AddTo(countCmd);
                scopeCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
            }

            long deleted;
            using (var deleteCmd = targetConnection.CreateTimedCommand())
            {
                var deleteScope = SegmentScope.Build(dialect, binder, segment, shape.Columns, columnMappings);
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = KeyReconcileDeleteStatement.BuildDelete(
                    dialect, shape.QuotedTarget, deleteScope.Predicate, staged.StagingLocation, shape.PrimaryKeyColumns);
                deleteScope.AddTo(deleteCmd);
                deleted = await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var result = DeleteGuardEvaluator.Check(guard, scopeCount, deleted);
            if (!result.Ok)
                throw new InvalidOperationException(result.Message);

            await transaction.CommitAsync(cancellationToken);
            return new WriteResult(deleted);
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

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Count the scope",
                KeyReconcileDeleteStatement.BuildCount(shape.QuotedTarget, scope.Predicate), PreviewOrigin.BuiltIn,
                "Establishes the denominator the delete guard compares against, before anything is removed."),

            new PreviewStatement(
                PreviewStages.Write, "Delete keys absent from the staged set",
                KeyReconcileDeleteStatement.BuildDelete(
                    dialect, shape.QuotedTarget, scope.Predicate, "<staging>", shape.PrimaryKeyColumns),
                PreviewOrigin.BuiltIn,
                guard is RatioDeleteGuard ratio
                    ? $"Guarded: rolled back if this would delete more than {ratio.MaxRatio:P0} of the scope's rows."
                    : "Unguarded — every row in scope absent from the staged set is deleted."),
        ];
    }
}

/// <summary>Statement text for <see cref="KeyReconcileDeleteWriter"/>, separated so it can be asserted
/// without a live server.</summary>
public static class KeyReconcileDeleteStatement
{
    public static string BuildCount(string quotedTarget, string scopePredicate) =>
        $"SELECT COUNT(*) FROM {quotedTarget} WHERE {scopePredicate}";

    /// <summary>
    /// A correlated <c>NOT EXISTS</c>, not a tuple <c>NOT IN</c> — the portable form across every
    /// dialect in scope, and the one that doesn't misbehave the moment a staged key column can be
    /// NULL (a tuple <c>NOT IN</c> against a set containing NULL matches nothing at all, silently).
    /// </summary>
    public static string BuildDelete(
        SqlDialect dialect, string quotedTarget, string scopePredicate, string stagingLocation,
        IReadOnlyList<string> keyColumns)
    {
        var join = string.Join(" AND ", keyColumns.Select(k =>
            $"s.{dialect.QuoteIdentifier(k)} = {quotedTarget}.{dialect.QuoteIdentifier(k)}"));

        return $"""
            DELETE FROM {quotedTarget}
            WHERE {scopePredicate}
              AND NOT EXISTS (SELECT 1 FROM {stagingLocation} s WHERE {join})
            """;
    }
}
