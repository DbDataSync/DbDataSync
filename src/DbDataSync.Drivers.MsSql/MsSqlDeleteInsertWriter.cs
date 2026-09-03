using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Replaces a segment's worth of target rows wholesale: delete everything in scope, insert the staged
/// change set. Reconciling, like <see cref="MsSqlMergeReconcileWriter"/>, but by replacement rather
/// than by matching — which makes it the one writer here that needs **no primary key**, since nothing
/// is ever joined row-by-row. For a wide reload of a keyless table, or where a MERGE's matching cost
/// outweighs simply rewriting the segment, this is the cheaper shape.
/// <para>
/// Both statements run inside one transaction, so no concurrent reader ever observes the segment
/// empty: a read during the write sees either the old contents or the new ones, never neither.
/// </para>
/// </summary>
public sealed class MsSqlDeleteInsertWriter : IChangeWriter, IStatementPreview
{
    public string Kind => MsSqlDriverKinds.DeleteInsert;

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
        targetConnection.ChangeDatabase(target.Database);

        var shape = MsSqlTargetShape.FromCachedColumns(mappingName, targetColumns, target, columnMappings);
        var scope = MsSqlSegmentScope.Build(SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var deleteCmd = targetConnection.CreateTimedCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = BuildDelete(shape, scope.Predicate);
                scope.AddTo(deleteCmd);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var rowsInserted = await MsSqlIdentityInsert.RunAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresIdentityInsert,
                async () =>
                {
                    using var insertCmd = targetConnection.CreateTimedCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = BuildInsert(shape, staged.StagingLocation);
                    return await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // RowsWritten counts the insert only. Folding the delete count in would double-count every
            // row that was simply replaced, which on a typical reload is most of them.
            return new WriteResult(rowsInserted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string BuildDelete(MsSqlTargetShape shape, string scopePredicate) =>
        $"DELETE FROM {shape.QuotedTarget} WHERE {scopePredicate};";

    /// <summary>
    /// Deletes in the change set are dropped rather than applied: the delete has already removed
    /// everything in scope, so a 'D' row would only be re-adding a row in order to say it isn't there.
    /// </summary>
    private static string BuildInsert(MsSqlTargetShape shape, string stagingLocation) => $"""
        INSERT INTO {shape.QuotedTarget} ({shape.InsertColumnList})
        SELECT {shape.InsertColumnList} FROM {stagingLocation}
        WHERE {MsSqlTargetShape.OperationColumn} <> 'D';
        """;

    /// <inheritdoc cref="DeleteInsertWriter.DescribeAsync"/>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);

        var shape = await MsSqlTargetShape.LoadAsync(
            request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var scope = MsSqlSegmentScope.Build(segment, shape.Columns, request.ColumnMappings);

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Empty the scope", BuildDelete(shape, scope.Predicate), PreviewOrigin.BuiltIn,
                segment is null
                    ? "Unsegmented, that scope is the whole table."
                    : $"Scoped to {segment.Describe()}; rows outside it are untouched."),

            new PreviewStatement(
                PreviewStages.Write, "Refill it from the staged rows",
                BuildInsert(shape, "#Staging_<per pass>"), PreviewOrigin.BuiltIn,
                "Both statements run in one transaction."),
        ];
    }
}
