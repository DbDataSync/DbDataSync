using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Replaces a segment's worth of target rows wholesale: delete everything in scope, insert the staged
/// change set.
/// <para>
/// **The portable writer.** Reconciling, but by replacement rather than by matching — so it needs no
/// primary key, joins nothing row by row, and is expressible identically on every engine in scope. An
/// upsert is where engines diverge hardest (<c>MERGE</c>, <c>ON CONFLICT</c>, <c>ON DUPLICATE KEY</c>,
/// and Oracle's own <c>MERGE</c> spelling); delete-then-insert is where they do not. That is what makes
/// this the writer a new driver gets for free.
/// </para>
/// <para>
/// It is also what makes watermark mode useful on an engine with no change data capture at all: a
/// watermark reader cannot see deletes, but a segmented reload through this writer converges the
/// target anyway.
/// </para>
/// <para>
/// Both statements run inside one transaction, so no concurrent reader ever observes the segment
/// empty: a read during the write sees either the old contents or the new ones, never neither.
/// </para>
/// </summary>
public sealed class DeleteInsertWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder) : IChangeWriter
{
    public string Kind => GenericDriverKinds.DeleteInsert;

    public bool SupportsReconciliation => true;

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(dialect, catalog, targetConnection, target, columnMappings, cancellationToken);
        var scope = SegmentScope.Build(dialect, binder, SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var deleteCmd = targetConnection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = DeleteInsertStatement.BuildDelete(shape.QuotedTarget, scope.Predicate);
                scope.AddTo(deleteCmd);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var rowsInserted = await dialect.WriteWithGeneratedColumnOverrideAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresGeneratedColumnOverride,
                async () =>
                {
                    using var insertCmd = targetConnection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = DeleteInsertStatement.BuildInsert(
                        dialect, shape.QuotedTarget, shape.InsertColumnList, staged.StagingLocation);
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

/// <summary>Statement text for <see cref="DeleteInsertWriter"/>, separated so it can be asserted
/// without a live server.</summary>
public static class DeleteInsertStatement
{
    public static string BuildDelete(string quotedTarget, string scopePredicate) =>
        $"DELETE FROM {quotedTarget} WHERE {scopePredicate};";

    /// <summary>
    /// Deletes in the change set are dropped rather than applied: the delete has already removed
    /// everything in scope, so a 'D' row would only be re-adding a row in order to say it isn't there.
    /// </summary>
    public static string BuildInsert(SqlDialect dialect, string quotedTarget, string insertColumnList, string stagingLocation) =>
        $"""
        INSERT INTO {quotedTarget} ({insertColumnList})
        SELECT {insertColumnList} FROM {stagingLocation}
        WHERE {dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn)} <> 'D';
        """;
}
