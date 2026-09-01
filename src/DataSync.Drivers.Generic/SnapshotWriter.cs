using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Appends every staged row to the target, marked with when the snapshot ran.
///
/// <para>
/// **No comparison, no dedup, no delete.** Every pass writes every row it was given, always. A
/// snapshot's whole purpose is "everything, as it was at this moment", and a writer that skipped rows
/// that had not changed would produce snapshots that are not snapshots — the row missing from
/// Tuesday's copy would mean "unchanged" to somebody who knows the implementation and "deleted" to
/// everybody else.
/// </para>
///
/// <para>
/// Not reconciling, and cannot be: there is no notion of removing what is absent from a target whose
/// entire job is keeping what used to be there.
/// </para>
///
/// <para>
/// Naturally paired with a full read on a schedule rather than a change feed. Nothing prevents the
/// other pairing, and the binding UI suggests the one that makes sense.
/// </para>
/// </summary>
public sealed class SnapshotWriter(SqlDialect dialect, ITableCatalog catalog) : IChangeWriter, IStatementPreview
{
    public string Kind => GenericDriverKinds.Snapshot;

    public bool SupportsReconciliation => false;

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

        var shape = await TargetShape.LoadAsync(
            dialect, catalog, targetConnection, target, columnMappings, cancellationToken);

        using var cmd = targetConnection.CreateTimedCommand();
        cmd.CommandText = HistorizedStatement.BuildSnapshotInsert(
            dialect, shape.QuotedTarget, columnMappings, staged.StagingLocation);
        // One value for the whole pass, bound once: rows of one snapshot sharing a marker is what
        // makes "the most recent snapshot" a filter rather than a range somebody has to guess at.
        cmd.AddParameter(dialect.ParameterName("snapshotAt"), DateTimeOffset.UtcNow.UtcDateTime);

        return new WriteResult(await cmd.ExecuteNonQueryAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Target.Database, cancellationToken);
        var shape = await TargetShape.LoadAsync(
            dialect, catalog, request.Connection, request.Target, request.ColumnMappings, cancellationToken);

        return
        [
            new PreviewStatement(
                PreviewStages.Write,
                "Append every staged row as a new snapshot",
                HistorizedStatement.BuildSnapshotInsert(
                    dialect, shape.QuotedTarget, request.ColumnMappings, "<staging>"),
                PreviewOrigin.BuiltIn,
                "Nothing is compared and nothing is removed. Every pass appends a complete copy, which " +
                "is what makes each one a snapshot."),
        ];
    }
}
