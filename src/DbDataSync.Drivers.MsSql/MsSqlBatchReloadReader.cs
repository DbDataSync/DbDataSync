using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Reads a whole source table, or one <see cref="BatchReloadSegment"/> of it, for a batch reload —
/// the "reload this data from scratch" counterpart to the incremental readers.
/// <para>
/// Deliberately not incremental: <c>previousWatermark</c> is ignored outright, because a reload's
/// entire purpose is to re-read rows an incremental pass has already seen. Every row is yielded as
/// <see cref="ChangeOperation.Insert"/> — a full scan can only observe rows that exist, never ones
/// that were removed. Deletion is the *writer's* job on a reload: a reconciling writer
/// (<see cref="IChangeWriter.SupportsReconciliation"/>) removes target rows within the segment's scope
/// that this scan didn't produce, which is what makes a reload converge rather than only ever add.
/// </para>
/// <para>
/// Not to be confused with <see cref="MsSqlWatermarkReader"/>, which is the ongoing incremental
/// fallback for tables without Change Tracking metadata.
/// </para>
/// </summary>
public sealed class MsSqlBatchReloadReader : IChangeReader, ISegmentExpandingReader, IStatementPreview
{
    public string Kind => MsSqlDriverKinds.BatchReload;

    // No IReadIntentDeclaring: this reader has no incremental mode at all — every pass reloads,
    // whatever intent it is asked for — and nothing honest is left to declare once InitialLoad stops
    // being a per-reader question (phase 101's retargeted §1). A reader with nothing to say about
    // Changes/ChangesFromEarliest/ChangesFromLatest does not implement the interface, the same
    // convention ISegmentExpandingReader already follows for a reader that cannot expand a segment. See
    // BatchReloadReader's twin note.

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        sourceConnection.ChangeDatabase(source.Database);

        var segment = SegmentSerializer.ReadOptional(options);
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken);
        var scope = MsSqlSegmentScope.Build(segment, columns);

        var rows = ReadRowsAsync(
            sourceConnection, source, scope, SourceProjection.Render(MsSqlDialect.Instance, columnMappings), cancellationToken);

        // This reader has no watermark of its own to report. It echoes the previous one back rather
        // than inventing a value, so that a standalone reload replication — which runs as a Primary
        // pass, and whose Primary passes therefore do persist whatever comes back here — leaves the
        // stored watermark exactly as it found it instead of writing a meaningless one over it.
        return new ReadResult(rows, previousWatermark ?? "");
    }

    /// <inheritdoc cref="BatchReloadReader.DescribeAsync"/>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Source.Database);

        var segment = SegmentSerializer.ReadOptional(request.Options);
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(
            request.Connection, request.Source.Schema, request.Source.Table, cancellationToken);
        var scope = MsSqlSegmentScope.Build(segment, columns);

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                segment is null ? "Reload every row" : $"Reload the segment {segment.Describe()}",
                BatchReloadStatement.BuildRead(
                    MsSqlDialect.Instance, request.Source.Schema, request.Source.Table, scope.Predicate,
                    request.Source.Filter, SourceProjection.Render(MsSqlDialect.Instance, request.ColumnMappings)),
                PreviewOrigin.BuiltIn,
                segment is null
                    ? "A backfill supplies its own segment, which narrows this further — this is the " +
                      "unsegmented form the mapping's own config would run."
                    : null),
        ];
    }

    public async Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyList<BatchReloadSegment> segments,
        CancellationToken cancellationToken)
    {
        if (!segments.OfType<AutoSegment>().Any())
            return segments;

        sourceConnection.ChangeDatabase(source.Database);
        var columns = await MsSqlSchemaQueries.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken);

        var expanded = new List<BatchReloadSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is not AutoSegment auto)
            {
                expanded.Add(segment);
                continue;
            }

            var column = columns.FirstOrDefault(c => string.Equals(c.Name, auto.Column, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Auto segment column '{auto.Column}' was not found on '{source.Schema}.{source.Table}'.");

            var (min, max) = await GetRangeAsync(sourceConnection, source, column, cancellationToken);
            if (min is null || max is null)
            {
                // No rows to divide up. One Full segment, not zero segments: an empty source still has
                // to be *applied*, or a reconciling writer never gets the chance to clear the target.
                expanded.Add(new FullSegment());
                continue;
            }

            expanded.AddRange(SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, column.Name, column.NativeType, min, max, auto.BucketCount));
        }

        return expanded;
    }

    private static async Task<(object? Min, object? Max)> GetRangeAsync(
        DbConnection connection, SourceTableRef source, ColumnMetadata column, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRange(
            MsSqlDialect.Instance, source.Schema, source.Table, column.Name, source.Filter);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (null, null);

        return (reader.IsDBNull(0) ? null : reader.GetValue(0), reader.IsDBNull(1) ? null : reader.GetValue(1));
    }

    private static async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceTableRef source,
        SegmentScope scope,
        string projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The shared builder rather than a second copy of the same SQL: this reader differs from the
        // generic one in how it discovers columns and binds segment values, not in what it selects.
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRead(
            MsSqlDialect.Instance, source.Schema, source.Table, scope.Predicate, source.Filter, projection);
        scope.AddTo(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}
