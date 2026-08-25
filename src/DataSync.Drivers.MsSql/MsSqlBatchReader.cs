using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Generic fallback reader for tables without Change Tracking/CDC enabled: reads rows where a
/// configured watermark column exceeds the previous cursor. Cannot detect deletes — a row removed
/// from the source is simply never seen again, it does not surface as a Delete change. All rows are
/// tagged <see cref="ChangeOperation.Insert"/>; downstream writers that upsert treat Insert/Update
/// alike, so this only matters if a writer ever needs to distinguish them (none currently do).
/// </summary>
public sealed class MsSqlBatchReader : IChangeReader
{
    public string Kind => MsSqlDriverKinds.Batch;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousCursor,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("watermarkColumn", out var watermarkColumn) || string.IsNullOrWhiteSpace(watermarkColumn))
            throw new InvalidOperationException("The 'watermarkColumn' option is required for the Batch reader.");

        sourceConnection.ChangeDatabase(source.Database);

        var newCursor = await GetMaxWatermarkAsync(sourceConnection, source, watermarkColumn, cancellationToken)
            ?? previousCursor
            ?? "0";

        var rows = ReadRowsAsync(sourceConnection, source, watermarkColumn, previousCursor, cancellationToken);
        return new ReadResult(rows, newCursor);
    }

    private static async Task<string?> GetMaxWatermarkAsync(
        DbConnection connection, SourceTableRef source, string watermarkColumn, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        var filterClause = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE {source.Filter}";
        cmd.CommandText =
            $"SELECT MAX({SqlIdentifier.Quote(watermarkColumn)}) FROM " +
            $"{SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}{filterClause};";

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async IAsyncEnumerable<ChangeRow> ReadRowsAsync(
        DbConnection connection,
        SourceTableRef source,
        string watermarkColumn,
        string? previousCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var quotedColumn = SqlIdentifier.Quote(watermarkColumn);
        var predicate = previousCursor is null ? "1 = 1" : $"{quotedColumn} > @previousCursor";
        var userFilter = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" AND ({source.Filter})";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT * FROM {SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}
            WHERE {predicate}{userFilter}
            ORDER BY {quotedColumn};
            """;
        if (previousCursor is not null)
            cmd.AddParameter("@previousCursor", previousCursor);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return new ChangeRow(ChangeOperation.Insert, values);
        }
    }
}
