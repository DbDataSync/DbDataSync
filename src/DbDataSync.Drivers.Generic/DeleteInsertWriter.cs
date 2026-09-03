using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

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
/// <para>
/// **That is why this writer's chunking stops short of what <see cref="ApplyBatch"/> buys elsewhere.**
/// The refill is issued a chunk at a time — smaller statements, so a huge reload no longer builds one
/// enormous insert — but every chunk stays inside the single transaction the delete opened. Committing
/// per chunk would publish a half-refilled segment to anyone reading the target, which for a writer
/// whose entire contract is "replace this scope atomically" is not a tuning knob, it is a different
/// writer. An upsert writer has no such invariant to protect and does commit per chunk; see
/// <c>MsSqlMergeWriter</c>.
/// </para>
/// </summary>
public sealed class DeleteInsertWriter(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.DeleteInsert;

    public bool SupportsReconciliation => true;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [ApplyBatch.Descriptor];

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
        var scope = SegmentScope.Build(dialect, binder, SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var deleteCmd = targetConnection.CreateTimedCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = DeleteInsertStatement.BuildDelete(shape.QuotedTarget, scope.Predicate);
                scope.AddTo(deleteCmd);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var batchSize = ApplyBatch.Read(options);
            var rowsInserted = await dialect.WriteWithGeneratedColumnOverrideAsync(
                targetConnection, transaction, shape.QuotedTarget, shape.RequiresGeneratedColumnOverride,
                async () =>
                {
                    var inserted = 0;
                    foreach (var (after, upTo) in ApplyBatch.Ranges(staged.RowCount, batchSize))
                    {
                        using var insertCmd = targetConnection.CreateTimedCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = DeleteInsertStatement.BuildInsert(
                            dialect, shape.QuotedTarget, shape.InsertColumnList, staged.StagingLocation,
                            shape.RequiresGeneratedColumnOverride, chunked: batchSize is not null);
                        if (batchSize is not null)
                        {
                            insertCmd.AddParameter(
                                dialect.ParameterName(DeleteInsertStatement.AfterOrdinalParameter), after);
                            insertCmd.AddParameter(
                                dialect.ParameterName(DeleteInsertStatement.UpToOrdinalParameter), upTo);
                        }

                        inserted += await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    return inserted;
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

    /// <summary>Both halves, in one transaction: the scope is emptied and then refilled, which is what
    /// makes this writer reconciling and what makes the delete's predicate the thing to read closely.</summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var scope = SegmentScope.Build(dialect, binder, segment, shape.Columns, request.ColumnMappings);

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Empty the scope", 
                DeleteInsertStatement.BuildDelete(shape.QuotedTarget, scope.Predicate), PreviewOrigin.BuiltIn,
                segment is null
                    ? "Unsegmented, that scope is the whole table."
                    : $"Scoped to {segment.Describe()}; rows outside it are untouched."),

            new PreviewStatement(
                PreviewStages.Write, "Refill it from the staged rows",
                DeleteInsertStatement.BuildInsert(
                    dialect, shape.QuotedTarget, shape.InsertColumnList, "<staging>",
                    shape.RequiresGeneratedColumnOverride, chunked: ApplyBatch.Read(request.Options) is not null),
                PreviewOrigin.BuiltIn,
                ApplyBatch.Read(request.Options) is { } size
                    ? $"Issued once per {size} staged rows — but all of them inside the one transaction " +
                      "the delete opened, so the scope is never observed half-refilled."
                    : "Both statements run in one transaction."),
        ];
    }
}

/// <summary>Statement text for <see cref="DeleteInsertWriter"/>, separated so it can be asserted
/// without a live server.</summary>
public static class DeleteInsertStatement
{
    public static string BuildDelete(string quotedTarget, string scopePredicate) =>
        $"DELETE FROM {quotedTarget} WHERE {scopePredicate};";

    public const string AfterOrdinalParameter = "afterOrdinal";
    public const string UpToOrdinalParameter = "upToOrdinal";

    /// <summary>
    /// Deletes in the change set are dropped rather than applied: the delete has already removed
    /// everything in scope, so a 'D' row would only be re-adding a row in order to say it isn't there.
    /// </summary>
    /// <param name="chunked">
    /// Adds a range over the staging table's ordinal, so one call refills part of the scope rather
    /// than all of it. The ordinal is the staging table's key, which is what keeps a chunk a seek
    /// instead of another scan of everything staged.
    /// </param>
    public static string BuildInsert(
        SqlDialect dialect, string quotedTarget, string insertColumnList, string stagingLocation,
        bool overrideGenerated = false, bool chunked = false)
    {
        var ordinal = dialect.QuoteIdentifier(BatchInsertStagingProvider.OrdinalColumn);
        var bound = chunked
            ? $"\n  AND {ordinal} > {dialect.ParameterReference(AfterOrdinalParameter)}" +
              $"\n  AND {ordinal} <= {dialect.ParameterReference(UpToOrdinalParameter)}"
            : "";

        return $"""
            {dialect.RenderInsertInto(quotedTarget, insertColumnList, overrideGenerated)}
            SELECT {insertColumnList} FROM {stagingLocation}
            WHERE {dialect.QuoteIdentifier(BatchInsertStagingProvider.OperationColumn)} <> 'D'{bound};
            """;
    }
}
