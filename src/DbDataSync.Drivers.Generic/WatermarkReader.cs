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
public sealed class WatermarkReader(SqlDialect dialect, ITableCatalog catalog, ISegmentValueBinder binder)
    : IChangeReader, IStatementPreview, IReadIntentDeclaring
{
    public string Kind => GenericDriverKinds.Watermark;

    /// <summary>
    /// Not <see cref="ReadIntent.ChangesFromEarliest"/>: for this reader the feed <em>is</em> the table,
    /// so "read from the floor" is a full load under another name and offering it would be a button
    /// that lies. <see cref="ReadIntent.ChangesFromLatest"/> is the genuinely useful, asymmetric case —
    /// adopt a table as already-synced by storing <c>max(col)</c> without reading a row.
    /// </summary>
    public IReadOnlySet<ReadIntent> SupportedIntents { get; } =
        new HashSet<ReadIntent> { ReadIntent.InitialLoad, ReadIntent.Changes, ReadIntent.ChangesFromLatest };

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
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Watermark reader.");

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        // Adopts the table as already-synced: the highest value becomes the new watermark and nothing
        // is read at all — not even a bounded pass that happens to find nothing new, which still issues
        // the row read. See SupportedIntents.
        if (intent == ReadIntent.ChangesFromLatest)
        {
            var latest = await GetMaxWatermarkAsync(sourceConnection, source, watermarkColumn, cancellationToken)
                ?? previousWatermark ?? "0";
            return new ReadResult(EmptyRows(), latest, Diagnostics: null);
        }

        // InitialLoad reads the table itself, with no predicate — the same statement a first pass has
        // always issued, now driven by the intent rather than by previousWatermark being null.
        var incremental = intent == ReadIntent.Changes;
        var maxRows = BoundedRead.Read(options);

        // A bounded read does not ask for the source's current maximum, and must not: recording a
        // maximum this pass did not reach is exactly how capped reads lose rows. Its position comes
        // out of the rows themselves, so until they have streamed the honest answer is "no further
        // than where we already were".
        var newWatermark = maxRows is not null
            ? previousWatermark ?? "0"
            : await GetMaxWatermarkAsync(sourceConnection, source, watermarkColumn, cancellationToken)
              ?? previousWatermark
              ?? "0";

        // Resolved only when there is a bound to bind — an initial load has no predicate, so it needs no
        // column type and should not pay for a cache lookup to learn one. Cache-only as of phase 91: no
        // live catalog call left in this path at all, so an unrefreshed mapping fails loudly here rather
        // than querying the source.
        var column = incremental
            ? sourceColumns.RequireColumn(mappingName, "source", watermarkColumn)
            : null;

        var effectivePreviousWatermark = incremental ? previousWatermark : null;
        var projection = SourceProjection.Render(dialect, columnMappings);
        var bounded = maxRows is null ? null : new BoundedReadPosition();
        var rows = ReadRowsAsync(
            sourceConnection, source, watermarkColumn, effectivePreviousWatermark, column, projection, maxRows,
            bounded, cancellationToken);
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

        var statements = new List<PreviewStatement>();

        // A bounded pass genuinely does not issue this one: the row read is its own boundary
        // computation, so showing a MAX here would describe a statement that never runs.
        if (maxRows is null)
        {
            statements.Add(new PreviewStatement(
                PreviewStages.SourceRead,
                $"Read the highest '{watermarkColumn}', which becomes the next pass's watermark",
                WatermarkStatement.BuildMaxWatermark(dialect, source.Schema, source.Table, watermarkColumn, source.Filter),
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
            var column = (await catalog.GetColumnsAsync(request.Connection, source.Schema, source.Table, cancellationToken))
                .FirstOrDefault(c => string.Equals(c.Name, watermarkColumn, StringComparison.OrdinalIgnoreCase));
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
                dialect, source.Schema, source.Table, watermarkColumn, incremental, source.Filter,
                SourceProjection.Render(dialect, request.ColumnMappings), bounded: maxRows is not null),
            PreviewOrigin.BuiltIn,
            string.IsNullOrEmpty(boundNote + bindingNote) ? null : (boundNote + bindingNote).TrimEnd(),
            dialect.RenderDeclarations(parameters)));

        return statements;
    }

    private async Task<string?> GetMaxWatermarkAsync(
        DbConnection connection, SourceTableRef source, string watermarkColumn, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = WatermarkStatement.BuildMaxWatermark(
            dialect, source.Schema, source.Table, watermarkColumn, source.Filter);

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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = WatermarkStatement.BuildRead(
            dialect, source.Schema, source.Table, watermarkColumn, previousWatermark is not null, source.Filter,
            projection, bounded: maxRows is not null);
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
