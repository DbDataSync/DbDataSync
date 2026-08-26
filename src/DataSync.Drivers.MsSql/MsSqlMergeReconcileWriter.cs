using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Makes the target *match* the change set within one segment's scope: rows are inserted, updated, and
/// — unlike <see cref="MsSqlMergeWriter"/> — deleted when they exist in the target's scope but not in
/// the change set. This is what lets a batch reload converge, since a full-scan reader can only report
/// rows that exist and never ones that were removed at the source.
/// <para>
/// The scope is applied by a CTE that pre-filters the target *before* MERGE evaluates matching, rather
/// than by a predicate inside a WHEN clause. The distinction matters: a WHEN-clause filter still makes
/// the engine compare the change set against the entire target table (and consider every row of it for
/// <c>NOT MATCHED BY SOURCE</c>), so the segment bounds the rows *affected* while doing nothing to
/// bound what is scanned and locked. Filtering in the CTE is what makes a segmented reload actually
/// cheap, and keeps the delete side from ranging over rows the segment was supposed to exclude.
/// </para>
/// </summary>
public sealed class MsSqlMergeReconcileWriter : IChangeWriter
{
    public string Kind => MsSqlDriverKinds.MergeReconcile;

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
        var onClause = shape.BuildMergeOnClause();
        var scope = MsSqlSegmentScope.Build(SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        using var cmd = targetConnection.CreateCommand();
        // SELECT * rather than just the mapped columns: the CTE has to stay updatable for MERGE to use
        // it as a target, and projecting a subset of columns is one of the things that stops it being.
        cmd.CommandText = $"""
            ;WITH TargetScope AS (
                SELECT * FROM {shape.QuotedTarget} WHERE {scope.Predicate}
            )
            MERGE INTO TargetScope AS tgt
            USING {staged.StagingLocation} AS src
            ON {onClause}
            WHEN MATCHED AND src.{MsSqlTargetShape.OperationColumn} = 'D' THEN DELETE
            {shape.BuildUpdateClause()}
            WHEN NOT MATCHED BY TARGET AND src.{MsSqlTargetShape.OperationColumn} <> 'D'
                THEN INSERT ({shape.InsertColumnList}) VALUES ({shape.SourceValueList})
            WHEN NOT MATCHED BY SOURCE THEN DELETE;
            """;
        scope.AddTo(cmd);

        var rowsAffected = await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape,
            () => cmd.ExecuteNonQueryAsync(cancellationToken),
            cancellationToken);

        return new WriteResult(rowsAffected);
    }
}
