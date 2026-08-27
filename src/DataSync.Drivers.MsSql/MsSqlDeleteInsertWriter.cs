using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

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
public sealed class MsSqlDeleteInsertWriter : IChangeWriter
{
    public string Kind => MsSqlDriverKinds.DeleteInsert;

    public bool SupportsReconciliation => true;

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        targetConnection.ChangeDatabase(target.Database);

        var shape = await MsSqlTargetShape.LoadAsync(targetConnection, target, columnMappings, cancellationToken);
        var scope = MsSqlSegmentScope.Build(SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var deleteCmd = targetConnection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = $"DELETE FROM {shape.QuotedTarget} WHERE {scope.Predicate};";
                scope.AddTo(deleteCmd);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var rowsInserted = await MsSqlIdentityInsert.RunAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresIdentityInsert,
                async () =>
                {
                    using var insertCmd = targetConnection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    // Deletes in the change set are dropped rather than applied: the delete above has
                    // already removed everything in scope, so a 'D' row would only be re-adding a row
                    // in order to say it isn't there.
                    insertCmd.CommandText = $"""
                        INSERT INTO {shape.QuotedTarget} ({shape.InsertColumnList})
                        SELECT {shape.InsertColumnList} FROM {staged.StagingLocation}
                        WHERE {MsSqlTargetShape.OperationColumn} <> 'D';
                        """;
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
}
