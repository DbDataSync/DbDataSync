using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

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

    /// <summary>Only the batch size: everything else this writer does is derived from the target.</summary>
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
        targetConnection.ChangeDatabase(target.Database);

        var shape = MsSqlTargetShape.FromCachedColumns(mappingName, targetColumns, target, columnMappings);
        var batchSize = ApplyBatch.Read(options);

        long rowsAffected = 0;
        // Each chunk is its own statement and therefore its own implicit transaction, which is the
        // whole point: the target's locks are taken and released a chunk at a time instead of being
        // held across the entire staged set. An interrupted apply leaves earlier chunks committed —
        // safe here because a MERGE of the same staged rows is idempotent and the watermark has not
        // advanced, so the next attempt replays the pass and converges. See ApplyBatch.
        await MsSqlIdentityInsert.RunAsync(
            targetConnection, transaction: null, shape.QuotedTarget, shape.RequiresIdentityInsert,
            async () =>
            {
                foreach (var (after, upTo) in ApplyBatch.Ranges(staged.RowCount, batchSize))
                {
                    using var cmd = targetConnection.CreateTimedCommand();
                    cmd.CommandText = BuildMerge(shape, staged.StagingLocation, chunked: batchSize is not null);
                    if (batchSize is not null)
                    {
                        cmd.AddParameter($"@{AfterOrdinalParameter}", after);
                        cmd.AddParameter($"@{UpToOrdinalParameter}", upTo);
                    }

                    rowsAffected += await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                return rowsAffected;
            },
            cancellationToken);

        return new WriteResult(rowsAffected);
    }

    public const string AfterOrdinalParameter = "afterOrdinal";
    public const string UpToOrdinalParameter = "upToOrdinal";

    /// <summary>The statement itself, so the preview shows what runs rather than a reconstruction of
    /// it.</summary>
    /// <param name="chunked">
    /// When set, the staged set is read through a bounded subquery rather than directly. The bound is
    /// a range over the staging table's clustered ordinal, so each chunk seeks its own slice instead
    /// of re-scanning the whole staged set — without which chunking would be quadratic in the number
    /// of chunks.
    /// </param>
    internal static string BuildMerge(MsSqlTargetShape shape, string stagingLocation, bool chunked = false)
    {
        var source = chunked
            ? $"""
              (SELECT * FROM {stagingLocation}
                   WHERE {MsSqlTargetShape.OrdinalColumn} > @{AfterOrdinalParameter}
                     AND {MsSqlTargetShape.OrdinalColumn} <= @{UpToOrdinalParameter})
              """
            : stagingLocation;

        return $"""
            MERGE INTO {shape.QuotedTarget} AS tgt
            USING {source} AS src
            ON {shape.BuildMergeOnClause()}
            WHEN MATCHED AND src.{MsSqlTargetShape.OperationColumn} = 'D' THEN DELETE
            {shape.BuildUpdateClause()}
            WHEN NOT MATCHED BY TARGET AND src.{MsSqlTargetShape.OperationColumn} <> 'D'
                THEN INSERT ({shape.InsertColumnList}) VALUES ({shape.SourceValueList});
            """;
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);

        var shape = await MsSqlTargetShape.LoadAsync(
            request.Connection, request.Target, request.ColumnMappings, cancellationToken);

        var batchSize = ApplyBatch.Read(request.Options);
        var notes = new List<string>();
        if (batchSize is { } size)
            notes.Add($"Issued once per {size}-row chunk of the staged set, each its own transaction, " +
                      "so the target's locks are held for one chunk rather than the whole apply.");
        if (shape.RequiresIdentityInsert)
            notes.Add("Wrapped in SET IDENTITY_INSERT ON/OFF — a mapped identity column means the " +
                      "source's own values are written rather than the target generating new ones.");

        return
        [
            new PreviewStatement(
                PreviewStages.Write, "Merge the staged rows into the target",
                BuildMerge(shape, "#Staging_<per pass>", chunked: batchSize is not null), PreviewOrigin.BuiltIn,
                notes.Count == 0 ? null : string.Join(" ", notes)),
        ];
    }
}
