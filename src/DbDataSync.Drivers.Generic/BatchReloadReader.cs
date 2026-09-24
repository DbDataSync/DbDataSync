using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Reads a whole source table, or one <see cref="BatchReloadSegment"/> of it, for a batch reload.
/// Engine-neutral: an ordinary <c>SELECT</c> with a predicate, which is why it needs nothing from the
/// engine but quoting and placeholders — no catalog reference of its own since phase 167V; every column
/// lookup comes from the mapping's cache (<c>ReadChangesAsync</c>, phase 91) or a pre-resolved
/// <c>PreviewRequest</c> field (<c>DescribeAsync</c>), never a live call this class makes itself.
/// <para>
/// Deliberately not incremental: <c>previousWatermark</c> is ignored outright, because a reload's
/// entire purpose is to re-read rows an incremental pass has already seen. Every row is yielded as
/// <see cref="ChangeOperation.Insert"/> — a full scan can only observe rows that exist, never ones
/// that were removed. Deletion is the *writer's* job on a reload: a reconciling writer removes target
/// rows within the segment's scope that this scan didn't produce, which is what makes a reload
/// converge rather than only ever add.
/// </para>
/// </summary>
public sealed class BatchReloadReader(SqlDialect dialect, ISegmentValueBinder binder, string kind = GenericDriverKinds.BatchReload)
    : IChangeReader, ISegmentExpandingReader, IStatementPreview
{
    /// <summary>
    /// <paramref name="kind"/> (phase 191S) lets one engine register this reader under more than one
    /// Kind string — <c>MsSqlDriver</c> registers it under both <see cref="GenericDriverKinds.BatchReload"/>
    /// and its own pre-existing <c>"MsSqlBatchReload"</c>, the retired <c>MsSqlBatchReloadReader</c>'s
    /// Kind, so a mapping saved against that name keeps resolving to a real reader rather than losing its
    /// Kind outright — the same "one reader, more than one registered name" shape
    /// <see cref="DbDataSync.Drivers.Generic.RawQueryReader"/> already uses for the identical reason.
    /// </summary>
    public string Kind => kind;

    /// <summary>A reload reports what exists, not what was removed; the reconciling writer covers the
    /// rest. Stated so the UI can say so rather than infer it.</summary>
    public bool DetectsDeletes => false;

    /// <summary>The column a segment predicate scopes, or null for a full/no segment — <see
    /// cref="AutoSegment"/> never reaches this reader unexpanded (see <see cref="ExpandAutoSegmentsAsync"/>),
    /// so it isn't a case here.</summary>
    private static string? SegmentColumnOf(BatchReloadSegment? segment) => segment switch
    {
        ListSegment list => list.Column,
        RangeSegment range => range.Column,
        _ => null,
    };

    // No IReadIntentDeclaring: this reader has no incremental mode at all — every pass reloads,
    // whatever intent it is asked for. Its only checkmark in phase 101's original §1 table was
    // InitialLoad, which stopped being a per-reader question when the bulk-load retarget made it
    // universally available — so there is nothing left here to honestly declare, and
    // IReadIntentDeclaring's own doc says a reader in that position should not implement the interface
    // at all, the same convention ISegmentExpandingReader already follows for a reader that cannot
    // expand a segment.

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var segment = SegmentSerializer.ReadOptional(options);

        // A query-shaped source with subqueries disallowed gets none of segmenting, relationships, or
        // (RunExecutor's own, already-applied) column-expression transforms — the plain query still
        // runs, silently, rather than failing. See SourceTableSpec.AllowSubquery's own doc comment.
        var canWrap = source.Query is null || source.AllowSubquery;
        var effectiveRelationships = canWrap ? relationships : [];
        var effectiveSegment = canWrap ? segment : null;

        var relationshipAliases = RelationshipAliases.Assign(effectiveRelationships, columnMappings);
        var wrapForFeatures = effectiveSegment is not (null or FullSegment) ||
            columnMappings.Any(m => !string.IsNullOrWhiteSpace(m.Transform));
        var willWrap = source.Query is not null && (wrapForFeatures || relationshipAliases.Count > 0);
        var primaryReference = RelationshipAliases.PrimaryReference(dialect, relationshipAliases, sourceIsQuery: willWrap);
        var segmentReference = SegmentColumnOf(effectiveSegment) is { } segmentColumn
            ? SegmentScope.TransformAwareReference(segmentColumn, columnMappings, primaryReference ?? dialect.QuoteIdentifier)
            : primaryReference;

        // Cache-only as of phase 91 — ExpandAutoSegmentsAsync (a different method entirely) is the one
        // place in this reader that still asks the live catalog, because it samples the column's actual
        // value distribution, which no cache could substitute for. This read of a segment's *shape* has
        // no such excuse. Resolved only for a segment that actually names a column: SegmentScope.Build
        // never consults it for null/FullSegment, so a plain reload shouldn't have to pay for a
        // populated cache it doesn't need.
        IReadOnlyList<ColumnMetadata> columns = effectiveSegment is ListSegment or RangeSegment
            ? sourceColumns.RequireAll(mappingName, "source")
            : [];
        var scope = SegmentScope.Build(dialect, binder, effectiveSegment, columns, reference: segmentReference);

        var projection = SourceProjection.Render(dialect, columnMappings, primaryReference, relationshipAliases);
        var rows = ReadRowsAsync(
            sourceConnection, source, scope, projection, wrapForFeatures, effectiveRelationships, relationshipAliases,
            cancellationToken);

        // This reader has no watermark of its own to report. It echoes the previous one back rather
        // than inventing a value, so that a standalone reload replication — which runs as a Primary
        // pass, and whose Primary passes therefore do persist whatever comes back here — leaves the
        // stored watermark exactly as it found it instead of writing a meaningless one over it.
        return new ReadResult(rows, previousWatermark ?? "");
    }

    /// <summary>
    /// The read as it would be issued for whatever segment the options carry — which, for a preview
    /// taken from a mapping's saved config, is normally none. A reload's statement depends on its
    /// segment, so an unsegmented preview says so rather than implying a bulk load would run this.
    /// </summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        await dialect.UseDatabaseAsync(request.Connection, request.Source.Database, cancellationToken);

        var source = request.Source;
        var segment = SegmentSerializer.ReadOptional(request.Options);
        var canWrap = source.Query is null || source.AllowSubquery;
        var effectiveRelationships = canWrap ? request.Relationships : [];
        var effectiveSegment = canWrap ? segment : null;

        var relationshipAliases = RelationshipAliases.Assign(effectiveRelationships, request.ColumnMappings);
        var wrapForFeatures = effectiveSegment is not (null or FullSegment) ||
            request.ColumnMappings.Any(m => !string.IsNullOrWhiteSpace(m.Transform));
        var willWrap = source.Query is not null && (wrapForFeatures || relationshipAliases.Count > 0);
        var primaryReference = RelationshipAliases.PrimaryReference(dialect, relationshipAliases, sourceIsQuery: willWrap);
        var segmentReference = SegmentColumnOf(effectiveSegment) is { } segmentColumn
            ? SegmentScope.TransformAwareReference(segmentColumn, request.ColumnMappings, primaryReference ?? dialect.QuoteIdentifier)
            : primaryReference;

        // request.SourceColumns — phase 167V. Resolved once by PreviewService through ScriptedMetadata
        // (so a bound metadataProvider script is honoured), not a live catalog.GetColumnsAsync call here.
        var scope = SegmentScope.Build(dialect, binder, effectiveSegment, request.SourceColumns, reference: segmentReference);

        return
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                segment is null ? "Reload every row" : $"Reload the segment {segment.Describe()}",
                BatchReloadStatement.BuildRead(
                    dialect, source.Schema, source.Table, source.Query, scope.Predicate, source.Filter,
                    SourceProjection.Render(dialect, request.ColumnMappings, primaryReference, relationshipAliases),
                    wrapForFeatures, effectiveRelationships, relationshipAliases),
                PreviewOrigin.BuiltIn,
                segment is null
                    ? "A bulk load supplies its own segment, which narrows this further — this is the " +
                      "unsegmented form the mapping's own config would run."
                    : null),
        ];
    }

    public async Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyList<BatchReloadSegment> segments,
        IReadOnlyList<CachedColumn> sourceColumns,
        string mappingName,
        IReadOnlyList<ColumnMapping> columnMappings,
        CancellationToken cancellationToken)
    {
        if (!segments.OfType<AutoSegment>().Any())
            return segments;

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        // Soft unavailability, the same as any other segment on a source with subqueries disallowed:
        // there is no way to sample a range without wrapping the query, so an auto segment simply
        // cannot be expanded — one Full segment stands in for it, exactly like an empty observed range.
        if (source.Query is not null && !source.AllowSubquery)
            return segments.Select(BatchReloadSegment (s) => s is AutoSegment ? new FullSegment() : s).ToList();

        var expanded = new List<BatchReloadSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is not AutoSegment auto)
            {
                expanded.Add(segment);
                continue;
            }

            // The column's type — cache-only, phase 167V: no live catalog call here has a defense the
            // way GetRangeAsync's own live sampling below does. sourceColumns already holds every column
            // of the table (MappingColumnReader captures the whole catalog answer, not just mapped
            // columns), so an auto-segment column that isn't itself individually mapped is still here.
            var column = sourceColumns.RequireColumn(mappingName, "source", auto.Column);
            var transform = columnMappings.FirstOrDefault(m =>
                m.Relationship is null && string.Equals(m.SourceColumn, auto.Column, StringComparison.OrdinalIgnoreCase))?.Transform;

            var (min, max) = await GetRangeAsync(sourceConnection, source, column, transform, cancellationToken);
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
        DbConnection connection, SourceTableRef source, ColumnMetadata column, string? transform,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRange(
            dialect, source.Schema, source.Table, source.Query, column.Name, source.Filter, transform);

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
        bool wrapQuery,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, string> relationshipAliases,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = BatchReloadStatement.BuildRead(
            dialect, source.Schema, source.Table, source.Query, scope.Predicate, source.Filter, projection,
            wrapQuery, relationships, relationshipAliases);
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
    /// <para>
    /// <paramref name="relationships"/>/<paramref name="relationshipAliases"/> (phase 187J) add one
    /// <c>LEFT JOIN</c> per relationship actually referenced by a <see cref="ColumnMapping"/> — see
    /// <see cref="RelationshipAliases.Assign"/>. The primary table only gets its own <c>AS base</c>
    /// alias once at least one join is present, so a mapping with no relationships renders byte-for-byte
    /// what it always has.
    /// </para>
    /// <para>
    /// **Phase 191S**: <paramref name="query"/>, when set, replaces <paramref name="table"/> as the
    /// primary source, wrapped as a derived table — <c>(&lt;query&gt;) AS base</c> — but *only* when
    /// <paramref name="wrapQuery"/> says something on this pass actually needs it (a real segment, a
    /// relationship join, or a generated column expression: the caller knows all three, this method
    /// only knows about the join half). Absent all three, the query runs completely unwrapped — the
    /// operator's own statement, byte-for-byte, with no projection, no predicate, and nothing to
    /// substitute a <c>{{column}}</c> transform into, the same way a hand-written query has always run.
    /// A query that can't be used as a subquery still works for a plain read; it only fails (an ordinary
    /// SQL error) on the specific pass that needed to wrap it.
    /// </para>
    /// </summary>
    public static string BuildRead(
        SqlDialect dialect, string schema, string table, string? query, string scopePredicate, string? filter,
        string projection = "*", bool wrapQuery = false,
        IReadOnlyList<RelationshipConfig>? relationships = null,
        IReadOnlyDictionary<string, string>? relationshipAliases = null)
    {
        var joins = RelationshipJoins.Render(dialect, relationships, relationshipAliases);

        // Unwrapped: nothing on this pass needs the query to be anything but exactly what the operator
        // wrote. Checked before anything else runs, since none of the rest of this method's output would
        // be meaningful otherwise.
        if (query is not null && joins.Length == 0 && !wrapQuery)
            return query;

        var userFilter = string.IsNullOrWhiteSpace(filter) ? "" : $" AND ({filter})";
        var sourceExpression = query is not null ? $"({query})" : dialect.QualifyTable(schema, table);
        var fromTable = joins.Length == 0 && query is null ? sourceExpression : $"{sourceExpression} AS base";

        return $"""
            SELECT {projection} FROM {fromTable}{joins}
            WHERE {scopePredicate}{userFilter}
            """;
    }

    /// <summary>
    /// The observed extent of the column an auto segment divides up.
    /// <para>
    /// **Phase 191S**: <paramref name="query"/>, when set, always wraps — unlike <see cref="BuildRead"/>,
    /// there is no "run the operator's own statement unwrapped" alternative for an aggregate: the whole
    /// point of this statement is the <c>MIN</c>/<c>MAX</c> it computes, which is not something a raw
    /// passthrough could produce. Relationship joins are not threaded through this statement yet — segmenting
    /// (auto or otherwise) by a relationship column is deferred, tracked in phase 195S.
    /// </para>
    /// <para>
    /// **Phase 192S**: <paramref name="transform"/>, when the auto-segmented column also carries a
    /// <see cref="ColumnMapping.Transform"/>, makes the sampled range agree with what the target actually
    /// stores — bucket boundaries computed against the raw column would land in the wrong value-space
    /// once <see cref="SourceProjection"/> starts projecting the transformed one.
    /// </para>
    /// </summary>
    public static string BuildRange(
        SqlDialect dialect, string schema, string table, string? query, string column, string? filter,
        string? transform = null)
    {
        var sourceExpression = query is not null ? $"({query})" : dialect.QualifyTable(schema, table);
        var fromTable = query is not null ? $"{sourceExpression} AS base" : sourceExpression;
        Func<string, string> reference = query is not null ? c => $"base.{dialect.QuoteIdentifier(c)}" : dialect.QuoteIdentifier;
        var quoted = SourceProjection.RenderExpression(column, transform, reference);
        var filterClause = string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}";
        return $"SELECT MIN({quoted}), MAX({quoted}) FROM {fromTable}{filterClause}";
    }
}
