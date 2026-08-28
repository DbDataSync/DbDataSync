using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Applies a staged change set via a single MERGE statement (architecture/detailed-design.md §3.5's
/// primary writer path). Requires the target's primary key column(s) to be included in
/// <see cref="ColumnMapping"/> — used both as the MERGE join key and to distinguish which mapped
/// columns are safe to include in the UPDATE SET list.
/// <para>
/// Upsert-only: it inserts, updates and applies the change set's explicit deletes, but a target row
/// the change set simply doesn't mention is left alone. That's exactly right for an incremental feed
/// (where "not mentioned" means "unchanged"), and exactly wrong for a reload (where it means "gone
/// from the source") — hence <see cref="SupportsReconciliation"/> being false, and
/// <see cref="MsSqlMergeReconcileWriter"/> existing.
/// </para>
/// </summary>
public sealed class MsSqlMergeWriter : IChangeWriter, IStatementPreview
{
    public string Kind => MsSqlDriverKinds.Merge;

    public bool SupportsReconciliation => false;

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

        using var cmd = targetConnection.CreateCommand();
        cmd.CommandText = BuildMerge(shape, staged.StagingLocation);

        var rowsAffected = await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape.QuotedTarget, shape.RequiresIdentityInsert,
            () => cmd.ExecuteNonQueryAsync(cancellationToken),
            cancellationToken);

        return new WriteResult(rowsAffected);
    }

    /// <summary>The statement itself, so the preview shows what runs rather than a reconstruction of
    /// it.</summary>
    private static string BuildMerge(MsSqlTargetShape shape, string stagingLocation) => $"""
        MERGE INTO {shape.QuotedTarget} AS tgt
        USING {stagingLocation} AS src
        ON {shape.BuildMergeOnClause()}
        WHEN MATCHED AND src.{MsSqlTargetShape.OperationColumn} = 'D' THEN DELETE
        {shape.BuildUpdateClause()}
        WHEN NOT MATCHED BY TARGET AND src.{MsSqlTargetShape.OperationColumn} <> 'D'
            THEN INSERT ({shape.InsertColumnList}) VALUES ({shape.SourceValueList});
        """;

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);

        var shape = await MsSqlTargetShape.LoadAsync(
            request.Connection, request.Target, request.ColumnMappings, cancellationToken);

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Merge the staged rows into the target",
                BuildMerge(shape, "#Staging_<per pass>"), PreviewOrigin.BuiltIn,
                shape.RequiresIdentityInsert
                    ? "Wrapped in SET IDENTITY_INSERT ON/OFF — a mapped identity column means the " +
                      "source's own values are written rather than the target generating new ones."
                    : null),
        ];
    }
}
