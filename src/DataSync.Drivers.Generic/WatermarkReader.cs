using System.Data.Common;
using System.Globalization;
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
public sealed class WatermarkReader(SqlDialect dialect) : IChangeReader
{
    public string Kind => GenericDriverKinds.Watermark;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Watermark reader.");

        await dialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);

        var newWatermark = await GetMaxWatermarkAsync(sourceConnection, source, watermarkColumn, cancellationToken)
            ?? previousWatermark
            ?? "0";

        var rows = ReadRowsAsync(sourceConnection, source, watermarkColumn, previousWatermark, cancellationToken);
        return new ReadResult(rows, newWatermark);
    }

    private async Task<string?> GetMaxWatermarkAsync(
        DbConnection connection, SourceTableRef source, string watermarkColumn, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WatermarkStatement.BuildMaxWatermark(
            dialect, source.Schema, source.Table, watermarkColumn, source.Filter);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceTableRef source,
        string watermarkColumn,
        string? previousWatermark,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WatermarkStatement.BuildRead(
            dialect, source.Schema, source.Table, watermarkColumn, previousWatermark is not null, source.Filter);
        if (previousWatermark is not null)
            cmd.AddParameter(dialect.ParameterName(WatermarkStatement.PreviousWatermarkParameter), previousWatermark);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var schema = ResultSetSchema.From(reader);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, schema, ResultSetSchema.ReadValues(reader, schema.Count));
    }
}
