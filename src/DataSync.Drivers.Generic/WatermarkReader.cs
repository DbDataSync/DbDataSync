using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

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
    : IChangeReader, IStatementPreview
{
    public string Kind => GenericDriverKinds.Watermark;

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
    ];

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Watermark reader.");

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var newWatermark = await GetMaxWatermarkAsync(sourceConnection, source, watermarkColumn, cancellationToken)
            ?? previousWatermark
            ?? "0";

        // Resolved only when there is a bound to bind — a first pass has no predicate, so it needs no
        // column type and should not pay for a catalog round trip to learn one.
        var column = previousWatermark is null
            ? null
            : (await catalog.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken))
                .FirstOrDefault(c => string.Equals(c.Name, watermarkColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Watermark column '{watermarkColumn}' was not found on '{source.Schema}.{source.Table}'.");

        var projection = SourceProjection.Render(dialect, columnMappings);
        var rows = ReadRowsAsync(sourceConnection, source, watermarkColumn, previousWatermark, column, projection, cancellationToken);
        return new ReadResult(rows, newWatermark);
    }

    /// <summary>
    /// Both statements a pass issues, in order: the one that decides the new watermark, then the one
    /// that reads the rows. The second is the interesting one — whether it carries a predicate at all
    /// depends on the stored watermark, and that is the difference between an incremental read and a
    /// full table scan.
    /// </summary>
    public Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue("watermarkColumn", out var watermarkColumn)
            || string.IsNullOrWhiteSpace(watermarkColumn))
        {
            return Task.FromResult<IReadOnlyList<PreviewStatement>>(
            [
                new PreviewStatement(
                    PreviewStages.SourceRead, "Read", null, PreviewOrigin.BuiltIn,
                    "The 'watermarkColumn' option is required for this reader and is not set, so this " +
                    "pass would fail before issuing a statement."),
            ]);
        }

        var source = request.Source;
        var incremental = request.PreviousWatermark is not null;

        return Task.FromResult<IReadOnlyList<PreviewStatement>>(
        [
            new PreviewStatement(
                PreviewStages.SourceRead,
                $"Read the highest '{watermarkColumn}', which becomes the next pass's watermark",
                WatermarkStatement.BuildMaxWatermark(dialect, source.Schema, source.Table, watermarkColumn, source.Filter),
                PreviewOrigin.BuiltIn,
                "Taken before the rows are read, not derived from them — a row written during the pass " +
                "must be picked up by the next one rather than silently skipped."),

            new PreviewStatement(
                PreviewStages.SourceRead,
                incremental
                    ? $"Read rows after watermark '{request.PreviousWatermark}'"
                    : "Read every row — no watermark stored yet",
                WatermarkStatement.BuildRead(
                    dialect, source.Schema, source.Table, watermarkColumn, incremental, source.Filter,
                    SourceProjection.Render(dialect, request.ColumnMappings)),
                PreviewOrigin.BuiltIn,
                incremental
                    ? $"{dialect.ParameterName(WatermarkStatement.PreviousWatermarkParameter)} is bound as " +
                      $"'{watermarkColumn}'s own type, not as text — a conversion on the column side would " +
                      "prevent the index seek this strategy depends on."
                    : null),
        ]);
    }

    private async Task<string?> GetMaxWatermarkAsync(
        DbConnection connection, SourceTableRef source, string watermarkColumn, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WatermarkStatement.BuildRead(
            dialect, source.Schema, source.Table, watermarkColumn, previousWatermark is not null, source.Filter, projection);
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
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}
