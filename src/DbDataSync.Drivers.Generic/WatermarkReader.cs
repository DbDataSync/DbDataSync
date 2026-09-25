using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Fallback reader for tables without change-tracking metadata: reads rows where a configured
/// watermark column exceeds the previous watermark. Engine-neutral — the statement it builds is
/// ordinary SQL, so only quoting and placeholder syntax come from the <see cref="SqlDialect"/>.
/// <para>
/// Cannot detect deletes — a row removed from the source is simply never seen again, it does not
/// surface as a Delete change. That makes it exactly right for append-only and append/update-only
/// tables, and wrong for a table whose rows are deleted; the reader declares this
/// (<see cref="IChangeReader.DetectsDeletes"/>) so callers and the UI can say so rather than guess
/// from the Kind string. All rows are tagged <see cref="ChangeOperation.Insert"/>; downstream writers
/// that upsert treat Insert/Update alike, so this only matters if a writer ever needs to distinguish
/// them (none currently do).
/// </para>
/// <para>
/// Not to be confused with a batch-reload reader, which re-reads a whole table (or one segment of it)
/// from scratch. This reader is the ongoing incremental-sync fallback; that one is the reload path.
/// </para>
/// </summary>
public sealed class WatermarkReader(SqlDialect dialect, ISegmentValueBinder binder)
    : IChangeReader, IStatementPreview, IReadIntentDeclaring, IPositionCapturing
{
    public string Kind => GenericDriverKinds.Watermark;

    /// <summary>
    /// Not <see cref="ReadIntent.ChangesFromEarliest"/>: for this reader the feed <em>is</em> the table,
    /// so "read from the floor" is a full load under another name and offering it would be a button
    /// that lies. <see cref="ReadIntent.ChangesFromLatest"/> is the genuinely useful, asymmetric case —
    /// adopt a table as already-synced by storing <c>max(col)</c> without reading a row.
    /// <c>InitialLoad</c> is no longer a per-reader question at all — see
    /// <see cref="IReadIntentDeclaring"/> — so it is not declared here either, identically to every
    /// other reader.
    /// </summary>
    public IReadOnlySet<ReadIntent> SupportedIntents { get; } =
        new HashSet<ReadIntent> { ReadIntent.Changes, ReadIntent.ChangesFromLatest };

    /// <summary>
    /// Exactly what <see cref="ReadIntent.ChangesFromLatest"/> above adopts — <c>MAX(watermarkColumn)</c>,
    /// the same aggregate <see cref="WatermarkStatement.BuildMaxWatermark"/> already builds for a preview
    /// and for that intent, now exposed through <see cref="IPositionCapturing"/> rather than duplicated.
    /// Requires the same <c>watermarkColumn</c> option every other call on this reader does.
    /// </summary>
    public async Task<CapturedPosition> CapturePositionAsync(
        DbConnection sourceConnection, SourceTableRef source, IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Watermark reader.");

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        // No ColumnMappings/RelationshipConfigs reach IPositionCapturing, unlike ReadChangesAsync — so a
        // captured position can't be made transform-aware, or resolve a relationship-sourced watermark
        // column's join/alias, here. A named gap, not an oversight, on both counts: see
        // WatermarkStatement.BuildMaxWatermark's own doc comment. A mapping whose watermarkRelationship
        // is set should avoid ChangesFromLatest until this is addressed.
        var latest = await GetMaxWatermarkAsync(
            sourceConnection, source, watermarkColumn, transform: null, relationships: [],
            relationshipAliases: new Dictionary<string, string>(), reference: null, cancellationToken) ?? "0";
        return new CapturedPosition(latest, PositionTimeUtc: null);
    }

    /// <summary>Required, and it is: without it this reader cannot run at all, which an operator
    /// previously found out on the first pass rather than while choosing the Kind.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new()
        {
            Name = "watermarkColumn",
            Label = "Watermark column",
            Description =
                "The column whose highest value marks how far this replication has read — a row " +
                "version, an identity, or a modified-at timestamp. Must be indexed to be worth using.",
            Type = ParameterType.ColumnPicker,
            Required = true,
        },
        new()
        {
            // Phase 195S. No dedicated relationship-picker UI yet (a real, separate frontend task) —
            // this only declares that the option exists, the same "additive, not a migration" posture
            // every parameter predating ParameterType had.
            Name = "watermarkRelationship",
            Label = "Watermark relationship",
            Description =
                "Optional. When set, watermarkColumn names a column on this declared relationship " +
                "instead of the primary source — re-emitting a row whenever its linked row's own " +
                "watermark advances, which a primary-column watermark cannot do. Every primary row " +
                "sharing one foreign row moves together on that foreign row's tick, so a bounded read's " +
                "row cap (maxRowsPerRead) is approximate here, not exact: the tie-safe guarantee still " +
                "holds — no row is ever split across passes — but a single foreign-row update can widen " +
                "one pass to however many primary rows point at it, which for a heavily-referenced " +
                "foreign row can be far more than the configured cap.",
            Type = ParameterType.Text,
            Required = false,
        },
        BoundedRead.Descriptor,
    ];

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, IReadOnlyList<CachedColumn>> relationshipColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Watermark reader.");
        var watermarkRelationship = options.GetValueOrDefault("watermarkRelationship");
        if (string.IsNullOrWhiteSpace(watermarkRelationship))
            watermarkRelationship = null;

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        // Phase 195S: a watermark used only to order/scope — projecting no column of its own — still
        // needs its relationship joined and aliased, the same reasoning as a segment's own Relationship.
        var relationshipAliases = RelationshipAliases.Assign(relationships, columnMappings, watermarkRelationship);
        var primaryReference = RelationshipAliases.PrimaryReference(
            dialect, relationshipAliases, sourceIsQuery: source.Query is not null);
        var watermarkReference = watermarkRelationship is null
            ? primaryReference
            : SourceProjection.ReferenceFor(
                dialect, watermarkRelationship, dialect.QuoteIdentifier, relationshipAliases,
                $"Watermark column '{watermarkColumn}'");

        // Phase 192S: the same expression SourceProjection would project for this column, so the
        // adopted/refreshed maximum agrees with what a transform actually produces — a bare MAX(raw
        // column) would record a position in the wrong value-space the moment the watermark column is
        // also transformed on its way to the target. Phase 195S: matched against watermarkRelationship
        // rather than hardcoded to the primary source, the same generalization the segment predicate got.
        var watermarkTransform = columnMappings.FirstOrDefault(m =>
            string.Equals(m.Relationship, watermarkRelationship, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.SourceColumn, watermarkColumn, StringComparison.OrdinalIgnoreCase))?.Transform;

        // Adopts the table as already-synced: the highest value becomes the new watermark and nothing
        // is read at all — not even a bounded pass that happens to find nothing new, which still issues
        // the row read. See SupportedIntents.
        if (intent == ReadIntent.ChangesFromLatest)
        {
            var latest = await GetMaxWatermarkAsync(
                sourceConnection, source, watermarkColumn, watermarkTransform, relationships, relationshipAliases,
                watermarkReference, cancellationToken) ?? previousWatermark ?? "0";
            return new ReadResult(EmptyRows(), latest, Diagnostics: null);
        }

        // InitialLoad reads the table itself, with no predicate — the same statement a first pass has
        // always issued, now driven by the intent rather than by previousWatermark being null.
        //
        // Phase 134 routes a Primary pass's InitialLoad to the Bulk Load pipeline instead of calling
        // this reader at all — but only for a reader that implements IPositionCapturing, which this one
        // does. Unlike the other three, this branch is deliberately left in place rather than deleted:
        // this reader is also a legitimate choice for a mapping's *Bulk Load* reader (RunKind.BulkLoad
        // always asks any reader for InitialLoad, regardless of position-capturing, since a reload has
        // no incremental cursor to consult either way), and for that caller a null previousWatermark
        // with no predicate is exactly a reload's own definition — see RunExecutor.RunMappingAsync.
        var incremental = intent == ReadIntent.Changes;
        var maxRows = BoundedRead.Read(options);

        // A bounded read does not ask for the source's current maximum, and must not: recording a
        // maximum this pass did not reach is exactly how capped reads lose rows. Its position comes
        // out of the rows themselves, so until they have streamed the honest answer is "no further
        // than where we already were".
        var newWatermark = maxRows is not null
            ? previousWatermark ?? "0"
            : await GetMaxWatermarkAsync(
                  sourceConnection, source, watermarkColumn, watermarkTransform, relationships, relationshipAliases,
                  watermarkReference, cancellationToken)
              ?? previousWatermark
              ?? "0";

        // Resolved only when there is a bound to bind — an initial load has no predicate, so it needs no
        // column type and should not pay for a cache lookup to learn one. Cache-only as of phase 91: no
        // live catalog call left in this path at all, so an unrefreshed mapping fails loudly here rather
        // than querying the source. Phase 195S: a relationship-sourced watermark resolves its column's
        // type from that relationship's own cache instead of the primary source's.
        ColumnMetadata? column = null;
        if (incremental)
        {
            if (watermarkRelationship is null)
            {
                column = sourceColumns.RequireColumn(mappingName, "source", watermarkColumn);
            }
            else
            {
                var side = $"relationship '{watermarkRelationship}'";
                var cached = relationshipColumns.TryGetValue(watermarkRelationship, out var found)
                    ? found
                    : throw new MetadataNotCachedException(mappingName, side);
                column = cached.RequireColumn(mappingName, side, watermarkColumn);
            }
        }

        var effectivePreviousWatermark = incremental ? previousWatermark : null;
        var projection = SourceProjection.Render(dialect, columnMappings, primaryReference, relationshipAliases);
        var bounded = maxRows is null ? null : new BoundedReadPosition();
        var rows = ReadRowsAsync(
            sourceConnection, source, watermarkColumn, effectivePreviousWatermark, column, projection, maxRows,
            bounded, relationships, relationshipAliases, watermarkTransform, watermarkReference, cancellationToken);
        return new ReadResult(rows, newWatermark, Diagnostics: null, Bounded: bounded);
    }

    private static async IAsyncEnumerable<ChangeRow> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Both statements a pass issues, in order: the one that decides the new watermark, then the one
    /// that reads the rows. The second is the interesting one — whether it carries a predicate at all
    /// depends on the stored watermark, and that is the difference between an incremental read and a
    /// full table scan.
    /// </summary>
    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue("watermarkColumn", out var watermarkColumn)
            || string.IsNullOrWhiteSpace(watermarkColumn))
        {
            return
            [
                new PreviewStatement(
                    PreviewStages.SourceRead, "Read", null, PreviewOrigin.BuiltIn,
                    "The 'watermarkColumn' option is required for this reader and is not set, so this " +
                    "pass would fail before issuing a statement."),
            ];
        }

        var source = request.Source;
        var incremental = request.PreviousWatermark is not null;
        var maxRows = BoundedRead.Read(request.Options);
        var watermarkRelationship = request.Options.GetValueOrDefault("watermarkRelationship");
        if (string.IsNullOrWhiteSpace(watermarkRelationship))
            watermarkRelationship = null;

        var statements = new List<PreviewStatement>();
        var watermarkTransform = request.ColumnMappings.FirstOrDefault(m =>
            string.Equals(m.Relationship, watermarkRelationship, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.SourceColumn, watermarkColumn, StringComparison.OrdinalIgnoreCase))?.Transform;

        var relationshipAliases = RelationshipAliases.Assign(request.Relationships, request.ColumnMappings, watermarkRelationship);
        var primaryReference = RelationshipAliases.PrimaryReference(
            dialect, relationshipAliases, sourceIsQuery: source.Query is not null);
        var watermarkReference = watermarkRelationship is null
            ? primaryReference
            : SourceProjection.ReferenceFor(
                dialect, watermarkRelationship, dialect.QuoteIdentifier, relationshipAliases,
                $"Watermark column '{watermarkColumn}'");

        // A bounded pass genuinely does not issue this one: the row read is its own boundary
        // computation, so showing a MAX here would describe a statement that never runs.
        if (maxRows is null)
        {
            statements.Add(new PreviewStatement(
                PreviewStages.SourceRead,
                $"Read the highest '{watermarkColumn}', which becomes the next pass's watermark",
                WatermarkStatement.BuildMaxWatermark(
                    dialect, source.Schema, source.Table, source.Query, watermarkColumn, source.Filter, watermarkTransform,
                    request.Relationships, relationshipAliases, watermarkReference),
                PreviewOrigin.BuiltIn,
                "Taken before the rows are read, not derived from them — a row written during the pass " +
                "must be picked up by the next one rather than silently skipped."));
        }

        var boundNote = maxRows is { } limit
            ? $"Capped at {limit} rows, ties included, so no two rows sharing the boundary " +
              $"'{watermarkColumn}' can be split across passes. The next watermark is the last row's " +
              "own value — a position this pass actually reached — and the remainder arrives on the " +
              "pass after it. "
            : "";

        var bindingNote = incremental
            ? $"{dialect.ParameterName(WatermarkStatement.PreviousWatermarkParameter)} is bound as " +
              $"'{watermarkColumn}'s own type, not as text — a conversion on the column side would " +
              "prevent the index seek this strategy depends on."
            : "";

        // The bound rather than described: an admin pasting @previousWatermark's bare name into a
        // query tool has nothing to run. Declared as the watermark column's own type, matching the
        // note above — and matching what ReadRowsAsync actually binds.
        List<PreviewParameter> parameters = [];
        if (incremental)
        {
            // request.SourceColumns/RelationshipColumns — phase 167V/195S. See
            // BatchReloadReader.DescribeAsync's identical comment.
            var candidates = watermarkRelationship is null
                ? request.SourceColumns
                : request.RelationshipColumns.TryGetValue(watermarkRelationship, out var found) ? found : [];
            var column = candidates.FirstOrDefault(c => string.Equals(c.Name, watermarkColumn, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                var bound = binder.CreateParameter(
                    dialect.ParameterName(WatermarkStatement.PreviousWatermarkParameter),
                    request.PreviousWatermark!, column);
                parameters.Add(new(
                    WatermarkStatement.PreviousWatermarkParameter, column.NativeType, dialect.RenderLiteral(bound.Value)));
            }
        }
        if (maxRows is { } cap)
            parameters.Add(new(BoundedRead.RowLimitParameter, "int", cap.ToString()));

        statements.Add(new PreviewStatement(
            PreviewStages.SourceRead,
            incremental
                ? $"Read rows after watermark '{request.PreviousWatermark}'"
                : "Read every row — no watermark stored yet",
            WatermarkStatement.BuildRead(
                dialect, source.Schema, source.Table, source.Query, watermarkColumn, incremental, source.Filter,
                SourceProjection.Render(dialect, request.ColumnMappings, primaryReference, relationshipAliases),
                bounded: maxRows is not null, relationships: request.Relationships, relationshipAliases: relationshipAliases,
                transform: watermarkTransform, reference: watermarkReference),
            PreviewOrigin.BuiltIn,
            string.IsNullOrEmpty(boundNote + bindingNote) ? null : (boundNote + bindingNote).TrimEnd(),
            dialect.RenderDeclarations(parameters)));

        return statements;
    }

    private async Task<string?> GetMaxWatermarkAsync(
        DbConnection connection, SourceTableRef source, string watermarkColumn, string? transform,
        IReadOnlyList<RelationshipConfig> relationships, IReadOnlyDictionary<string, string> relationshipAliases,
        Func<string, string>? reference, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = WatermarkStatement.BuildMaxWatermark(
            dialect, source.Schema, source.Table, source.Query, watermarkColumn, source.Filter, transform,
            relationships, relationshipAliases, reference);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : WatermarkValue.Format(result);
    }

    private async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceTableRef source,
        string watermarkColumn,
        string? previousWatermark,
        ColumnMetadata? column,
        string projection,
        int? maxRows,
        BoundedReadPosition? bounded,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, string> relationshipAliases,
        string? watermarkTransform,
        Func<string, string>? watermarkReference,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = WatermarkStatement.BuildRead(
            dialect, source.Schema, source.Table, source.Query, watermarkColumn, previousWatermark is not null,
            source.Filter, projection, bounded: maxRows is not null, relationships: relationships,
            relationshipAliases: relationshipAliases, transform: watermarkTransform, reference: watermarkReference);
        if (maxRows is { } limit)
            cmd.AddParameter(dialect.ParameterName(BoundedRead.RowLimitParameter), limit);
        if (previousWatermark is not null)
        {
            // Bound as the watermark column's own type, not as text. SQL Server would convert
            // implicitly and Postgres refuses outright ("operator does not exist: timestamp > text"),
            // but even where it works the conversion happens on the *column* side of the comparison,
            // which prevents the index seek the whole watermark strategy depends on.
            cmd.Parameters.Add(binder.CreateParameter(
                dialect.ParameterName(WatermarkStatement.PreviousWatermarkParameter), previousWatermark, column!));
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // A bounded read appends the watermark column to the select list, so the schema the rows carry
        // is every field but that last one — which stays out of the change set and only ever becomes
        // the new watermark.
        var schema = bounded is null
            ? ResultSetSchema.From(reader)
            : ResultSetSchema.FromLeading(reader, reader.FieldCount - 1);
        var positionOrdinal = reader.FieldCount - 1;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (bounded is not null && !reader.IsDBNull(positionOrdinal))
            {
                // Every row, not just the last: the loop cannot know which row is last, and each
                // overwrite is what leaves the final one standing. Rows arrive in watermark order, so
                // this only ever moves forward.
                bounded.Reached = WatermarkValue.Format(reader.GetValue(positionOrdinal));
            }

            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
        }
    }
}
