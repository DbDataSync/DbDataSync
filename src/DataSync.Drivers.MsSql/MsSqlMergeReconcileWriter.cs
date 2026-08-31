using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

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
public sealed class MsSqlMergeReconcileWriter : IChangeWriter, IStatementPreview
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
        var scope = MsSqlSegmentScope.Build(SegmentSerializer.ReadOptional(options), shape.Columns, columnMappings);

        using var cmd = targetConnection.CreateCommand();
        cmd.CommandText = BuildMerge(shape, scope.Predicate, staged.StagingLocation);
        scope.AddTo(cmd);

        var rowsAffected = await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape.QuotedTarget, shape.RequiresIdentityInsert,
            () => cmd.ExecuteNonQueryAsync(cancellationToken),
            cancellationToken);

        return new WriteResult(rowsAffected);
    }

    /// <summary>
    /// SELECT * rather than just the mapped columns: the CTE has to stay updatable for MERGE to use it
    /// as a target, and projecting a subset of columns is one of the things that stops it being.
    /// </summary>
    private static string BuildMerge(MsSqlTargetShape shape, string scopePredicate, string stagingLocation) => $"""
        ;WITH TargetScope AS (
            SELECT * FROM {shape.QuotedTarget} WHERE {scopePredicate}
        )
        MERGE INTO TargetScope AS tgt
        USING {stagingLocation} AS src
        ON {shape.BuildMergeOnClause()}
        WHEN MATCHED AND src.{MsSqlTargetShape.OperationColumn} = 'D' THEN DELETE
        {shape.BuildUpdateClause()}
        WHEN NOT MATCHED BY TARGET AND src.{MsSqlTargetShape.OperationColumn} <> 'D'
            THEN INSERT ({shape.InsertColumnList}) VALUES ({shape.SourceValueList})
        WHEN NOT MATCHED BY SOURCE THEN DELETE;
        """;

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
                PreviewStages.Write, "Merge the staged rows and remove anything else in scope",
                BuildMerge(shape, scope.Predicate, "#Staging_<per pass>"), PreviewOrigin.BuiltIn,
                segment is null
                    ? "WHEN NOT MATCHED BY SOURCE deletes within the scope this writer was given — " +
                      "unsegmented, that scope is the whole table."
                    : $"Scoped to {segment.Describe()}; rows outside it are untouched."),
        ];
    }
}
