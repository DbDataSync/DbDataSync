using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Reads a whole source table, or one <see cref="BatchReloadSegment"/> of it, for a batch reload.
/// Engine-neutral: an ordinary <c>SELECT</c> with a predicate, which is why it needs nothing from the
/// engine but quoting, placeholders and a catalog.
/// <para>
/// Deliberately not incremental: <c>previousWatermark</c> is ignored outright, because a reload's
/// entire purpose is to re-read rows an incremental pass has already seen. Every row is yielded as
/// <see cref="ChangeOperation.Insert"/> — a full scan can only observe rows that exist, never ones
/// that were removed. Deletion is the *writer's* job on a reload: a reconciling writer removes target
/// rows within the segment's scope that this scan didn't produce, which is what makes a reload
/// converge rather than only ever add.
/// </para>
/// </summary>
public sealed class BatchReloadReader(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeReader, ISegmentExpandingReader, IStatementPreview
{
    public string Kind => GenericDriverKinds.BatchReload;

    /// <summary>A reload reports what exists, not what was removed; the reconciling writer covers the
    /// rest. Stated so the UI can say so rather than infer it.</summary>
    public bool DetectsDeletes => false;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var segment = SegmentSerializer.ReadOptional(options);
        // Cache-only as of phase 91 — ExpandAutoSegmentsAsync (a different method entirely) is the one
        // place in this reader that still asks the live catalog, because it samples the column's actual
        // value distribution, which no cache could substitute for. This read of a segment's *shape* has
        // no such excuse. Resolved only for a segment that actually names a column: SegmentScope.Build
        // never consults it for null/FullSegment, so a plain reload shouldn't have to pay for a
        // populated cache it doesn't need.
        IReadOnlyList<ColumnMetadata> columns = segment is ListSegment or RangeSegment
            ? sourceColumns.RequireAll(mappingName, "source")
            : [];
        var scope = SegmentScope.Build(dialect, binder, segment, columns);

        var rows = ReadRowsAsync(sourceConnection, source, scope, SourceProjection.Render(dialect, columnMappings), cancellationToken);

        // This reader has no watermark of its own to report. It echoes the previous one back rather
        // than inventing a value, so that a standalone reload replication — which runs as a Primary
        // pass, and whose Primary passes therefore do persist whatever comes back here — leaves the
        // stored watermark exactly as it found it instead of writing a meaningless one over it.
        return new ReadResult(rows, previousWatermark ?? "");
    }

    /// <summary>
    /// The read as it would be issued for whatever segment the options carry — which, for a preview
    /// taken from a mapping's saved config, is normally none. A reload's statement depends on its
    /// segment, so an unsegmented preview says so rather than implying a backfill would run this.
    /// </summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Source.Database, cancellationToken);

        var segment = SegmentSerializer.ReadOptional(request.Options);
        var columns = await catalog.GetColumnsAsync(
            request.Connection, request.Source.Schema, request.Source.Table, cancellationToken);
        var scope = SegmentScope.Build(dialect, binder, segment, columns);

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                segment is null ? "Reload every row" : $"Reload the segment {segment.Describe()}",
                BatchReloadStatement.BuildRead(
                    dialect, request.Source.Schema, request.Source.Table, scope.Predicate, request.Source.Filter,
                    SourceProjection.Render(dialect, request.ColumnMappings)),
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

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);
        var columns = await catalog.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken);

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

            expanded.AddRange(SegmentExpansion.BuildBuckets(dialect, column.Name, column.NativeType, min, max, auto.BucketCount));
        }

        return expanded;
    }

    private async Task<(object? Min, object? Max)> GetRangeAsync(
        DbConnection connection, SourceTableRef source, ColumnMetadata column, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRange(dialect, source.Schema, source.Table, column.Name, source.Filter);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return (null, null);

        return (reader.IsDBNull(0) ? null : reader.GetValue(0), reader.IsDBNull(1) ? null : reader.GetValue(1));
    }

    private async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceTableRef source,
        SegmentScope scope,
        string projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRead(dialect, source.Schema, source.Table, scope.Predicate, source.Filter, projection);
        scope.AddTo(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}

/// <summary>Statement text for <see cref="BatchReloadReader"/>, separated so it can be asserted
/// without a live server.</summary>
public static class BatchReloadStatement
{
    /// <summary>
    /// The segment predicate and the mapping's own static Filter compose — a segment narrows a reload
    /// within whatever subset of the table the mapping was always scoped to, it doesn't replace it.
    /// </summary>
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string scopePredicate, string? filter, string projection = "*")
    {
        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";
        return $"""
            SELECT {projection} FROM {dialect.QualifyTable(schema, table)}
            WHERE {scopePredicate}{userFilter};
            """;
    }

    /// <summary>The observed extent of the column an auto segment divides up.</summary>
    public static string BuildRange(SqlDialect dialect, string schema, string table, string column, string? filter)
    {
        var quoted = dialect.QuoteIdentifier(column);
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return $"SELECT MIN({quoted}), MAX({quoted}) FROM {dialect.QualifyTable(schema, table)}{filterClause};";
    }
}
