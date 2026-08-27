using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

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
    : IChangeReader, ISegmentExpandingReader
{
    public string Kind => GenericDriverKinds.BatchReload;

    /// <summary>A reload reports what exists, not what was removed; the reconciling writer covers the
    /// rest. Stated so the UI can say so rather than infer it.</summary>
    public bool DetectsDeletes => false;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var segment = SegmentSerializer.ReadOptional(options);
        var columns = await catalog.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken);
        var scope = SegmentScope.Build(dialect, binder, segment, columns);

        var rows = ReadRowsAsync(sourceConnection, source, scope, cancellationToken);

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
        using var cmd = connection.CreateCommand();
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BatchReloadStatement.BuildRead(dialect, source.Schema, source.Table, scope.Predicate, source.Filter);
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
    public static string BuildRead(SqlDialect dialect, string schema, string table, string scopePredicate, string? filter)
    {
        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";
        return $"""
            SELECT * FROM {dialect.QualifyTable(schema, table)}
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
