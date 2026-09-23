using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Reads only a source table's primary-key values, in scope, for phase 124's delete-diff sweep — the
/// cheap alternative to a <see cref="BatchReloadReader"/> full row scan when all a pass needs to know
/// is which keys still exist. Engine-neutral, same as <see cref="BatchReloadReader"/>: an ordinary
/// <c>SELECT</c> of the key columns with a segment predicate.
/// <para>
/// Deliberately not incremental, for the same reason a reload isn't: <c>previousWatermark</c> is
/// ignored and echoed back. Every key is yielded as <see cref="ChangeOperation.Insert"/> — this reader
/// reports what exists, never what was removed. Deletion is <see cref="KeyReconcileDeleteWriter"/>'s
/// job: it anti-joins the target's keys against what this reader staged and removes whatever the
/// target has that this scan didn't produce.
/// </para>
/// </summary>
public sealed class KeyReconcileReader(SqlDialect dialect, ISegmentValueBinder binder)
    : IChangeReader, ISegmentExpandingReader, IStatementPreview
{
    public string Kind => GenericDriverKinds.KeyReconcile;

    /// <summary>A key-only scan reports what exists, not what was removed; the reconciling writer
    /// covers the rest — same posture as <see cref="BatchReloadReader.DetectsDeletes"/>.</summary>
    public bool DetectsDeletes => false;

    // No IReadIntentDeclaring: same reasoning as BatchReloadReader — every pass reads the whole scope
    // regardless of intent, so there is nothing honest left to declare about Changes/
    // ChangesFromEarliest/ChangesFromLatest.

    /// <summary>
    /// <paramref name="columnMappings"/> filtered to only those naming a primary-key column of
    /// <paramref name="sourceColumns"/>. Shared with <see cref="DbDataSync.TaskRunner.RunExecutor"/>,
    /// which needs the identical filtered list to keep what it stages in agreement with what this
    /// reader actually selects — a staging table built from the *full* mapping would try to bind
    /// columns this reader never read. Takes the columns already resolved (rather than resolving them
    /// itself) so both the cache-only run-time path and <see cref="DescribeAsync"/>'s live-catalog path
    /// can share it.
    /// </summary>
    public static IReadOnlyList<ColumnMapping> KeyColumnMappings(
        IReadOnlyList<ColumnMapping> columnMappings, IReadOnlyList<ColumnMetadata> sourceColumns, string mappingName)
    {
        var keyColumns = sourceColumns.Where(c => c.IsPrimaryKey).ToList();
        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}' has no primary key on its source — the KeyReconcile " +
                "reader needs one to know which rows to diff by. A keyless source stays on the " +
                "BatchReload + DeleteInsert path instead.");

        var keyNames = keyColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keyMappings = columnMappings.Where(m => keyNames.Contains(m.SourceColumn)).ToList();

        var mappedKeyNames = keyMappings.Select(m => m.SourceColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = keyColumns.Where(c => !mappedKeyNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (unmapped.Count > 0)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}' does not map source key column(s) {string.Join(", ", unmapped)}. " +
                "The KeyReconcile reader needs every source key column mapped so the target side can be " +
                "anti-joined on it.");

        return keyMappings;
    }

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
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        // Unlike BatchReloadReader, this always needs the cached columns regardless of segment mode —
        // determining which columns are keys requires them — so there is no unsegmented fast path that
        // skips the cache read the way an unsegmented reload does.
        var cachedColumns = sourceColumns.RequireAll(mappingName, "source");
        var keyMappings = KeyColumnMappings(columnMappings, cachedColumns, mappingName);
        var segment = SegmentSerializer.ReadOptional(options);
        var scope = SegmentScope.Build(dialect, binder, segment, cachedColumns);

        var rows = ReadRowsAsync(
            sourceConnection, source, scope, SourceProjection.Render(dialect, keyMappings), cancellationToken);

        // No watermark of its own — echoed back, same as BatchReloadReader, so an ordinary Primary
        // pass over this reader (if one ever exists) leaves the stored watermark untouched.
        return new ReadResult(rows, previousWatermark ?? "");
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Source.Database, cancellationToken);

        var segment = SegmentSerializer.ReadOptional(request.Options);
        // request.SourceColumns — phase 167V. See BatchReloadReader.DescribeAsync's identical comment.
        var scope = SegmentScope.Build(dialect, binder, segment, request.SourceColumns);

        var keyMappings = request.ColumnMappings.Count == 0
            ? request.ColumnMappings
            : KeyColumnMappings(request.ColumnMappings, request.SourceColumns, "(preview)");

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                segment is null ? "Read every key" : $"Read keys in the segment {segment.Describe()}",
                KeyReconcileStatement.BuildRead(
                    dialect, request.Source.Schema, request.Source.Table, scope.Predicate, request.Source.Filter,
                    SourceProjection.Render(dialect, keyMappings)),
                PreviewOrigin.BuiltIn,
                "Only the source's primary-key columns are read — a delete-diff sweep never touches the rest of the row."),
        ];
    }

    public async Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyList<BatchReloadSegment> segments,
        IReadOnlyList<CachedColumn> sourceColumns,
        string mappingName,
        CancellationToken cancellationToken)
    {
        if (!segments.OfType<AutoSegment>().Any())
            return segments;

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var expanded = new List<BatchReloadSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is not AutoSegment auto)
            {
                expanded.Add(segment);
                continue;
            }

            // Cache-only — phase 167V. See BatchReloadReader.ExpandAutoSegmentsAsync's identical comment.
            var column = sourceColumns.RequireColumn(mappingName, "source", auto.Column);

            var (min, max) = await GetRangeAsync(sourceConnection, source, column, cancellationToken);
            if (min is null || max is null)
            {
                // No rows to diff. One Full segment, not zero: an empty source still has to be applied,
                // or the reconciling writer never gets the chance to clear the target's scope.
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
        cmd.CommandText = KeyReconcileStatement.BuildRange(dialect, source.Schema, source.Table, column.Name, source.Filter);

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
        cmd.CommandText = KeyReconcileStatement.BuildRead(dialect, source.Schema, source.Table, scope.Predicate, source.Filter, projection);
        scope.AddTo(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}

/// <summary>Statement text for <see cref="KeyReconcileReader"/>, separated so it can be asserted
/// without a live server.</summary>
public static class KeyReconcileStatement
{
    /// <inheritdoc cref="BatchReloadStatement.BuildRead"/>
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string scopePredicate, string? filter, string projection)
    {
        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";
        return $"""
            SELECT {projection} FROM {dialect.QualifyTable(schema, table)}
            WHERE {scopePredicate}{userFilter}
            """;
    }

    /// <inheritdoc cref="BatchReloadStatement.BuildRange"/>
    public static string BuildRange(SqlDialect dialect, string schema, string table, string column, string? filter)
    {
        var quoted = dialect.QuoteIdentifier(column);
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return $"SELECT MIN({quoted}), MAX({quoted}) FROM {dialect.QualifyTable(schema, table)}{filterClause}";
    }
}
