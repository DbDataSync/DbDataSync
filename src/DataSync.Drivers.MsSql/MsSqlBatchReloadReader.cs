using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

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
public sealed class MsSqlBatchReloadReader : IChangeReader, ISegmentExpandingReader
{
    public string Kind => MsSqlDriverKinds.BatchReload;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
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
        var quoted = SqlIdentifier.Quote(column.Name);
        var filterClause = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE {source.Filter}";

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT MIN({quoted}), MAX({quoted}) FROM " +
            $"{SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}{filterClause};";

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
        // The segment predicate and the mapping's own static Filter compose — a segment narrows a
        // reload within whatever subset of the table the mapping was always scoped to, it doesn't
        // replace it.
        var userFilter = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" AND ({source.Filter})";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {projection} FROM {SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}
            WHERE {scope.Predicate}{userFilter};
            """;
        scope.AddTo(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}
