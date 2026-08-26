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
public sealed class MsSqlMergeWriter : IChangeWriter
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
        var onClause = shape.BuildMergeOnClause();

        using var cmd = targetConnection.CreateCommand();
        cmd.CommandText = $"""
            MERGE INTO {shape.QuotedTarget} AS tgt
            USING {staged.StagingLocation} AS src
            ON {onClause}
            WHEN MATCHED AND src.{MsSqlTargetShape.OperationColumn} = 'D' THEN DELETE
            {shape.BuildUpdateClause()}
            WHEN NOT MATCHED BY TARGET AND src.{MsSqlTargetShape.OperationColumn} <> 'D'
                THEN INSERT ({shape.InsertColumnList}) VALUES ({shape.SourceValueList});
            """;

        var rowsAffected = await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape,
            () => cmd.ExecuteNonQueryAsync(cancellationToken),
            cancellationToken);

        return new WriteResult(rowsAffected);
    }
}
