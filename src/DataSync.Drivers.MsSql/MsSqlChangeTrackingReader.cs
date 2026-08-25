using System.Data.Common;
using System.Runtime.CompilerServices;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Reads changes via SQL Server Change Tracking (architecture/detailed-design.md §3.5 — the
/// recommended starting reader, simpler to enable than CDC). Requires Change Tracking already
/// enabled on the database and table by an operator with the necessary permissions; this driver only
/// ever queries it, never enables it (keeps the app's own DB permissions to read-only + VIEW CHANGE
/// TRACKING, per §6's least-privilege guidance).
/// </summary>
public sealed class MsSqlChangeTrackingReader : IChangeReader
{
    public string Kind => MsSqlDriverKinds.ChangeTracking;

    public async Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousCursor,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        sourceConnection.ChangeDatabase(source.Database);

        var targetVersion = await GetCurrentVersionAsync(sourceConnection, cancellationToken);

        if (previousCursor is not null)
        {
            var minValidVersion = await GetMinValidVersionAsync(sourceConnection, source, cancellationToken);
            if (long.Parse(previousCursor) < minValidVersion)
            {
                throw new InvalidOperationException(
                    $"Change Tracking history for '{source.Schema}.{source.Table}' no longer covers " +
                    $"cursor '{previousCursor}' (minimum valid version is {minValidVersion}). " +
                    "A full resync is required — clear the stored watermark for this table.");
            }
        }

        var rows = previousCursor is null
            ? ReadFullLoadAsync(sourceConnection, source, cancellationToken)
            : ReadIncrementalAsync(sourceConnection, source, long.Parse(previousCursor), targetVersion, cancellationToken);

        return new ReadResult(rows, targetVersion.ToString());
    }

    private static async Task<long> GetCurrentVersionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_CURRENT_VERSION();";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task<long> GetMinValidVersionAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@qualifiedName));";
        cmd.AddParameter("@qualifiedName", $"{source.Schema}.{source.Table}");
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
            throw new InvalidOperationException(
                $"Change Tracking is not enabled for table '{source.Schema}.{source.Table}' in database '{source.Database}'.");
        return Convert.ToInt64(result);
    }

    private static async IAsyncEnumerable<ChangeRow> ReadFullLoadAsync(
        DbConnection connection, SourceTableRef source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        var filterClause = string.IsNullOrWhiteSpace(source.Filter) ? "" : $" WHERE {source.Filter}";
        // source.Filter is an admin-authored raw predicate from TableMappingConfig, not end-user
        // input — see the type's XML doc. It cannot be parameterized since it's an arbitrary
        // boolean expression, not a value.
        cmd.CommandText =
            $"SELECT * FROM {SqlIdentifier.Quote(source.Schema)}.{SqlIdentifier.Quote(source.Table)}{filterClause};";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return new ChangeRow(ChangeOperation.Insert, ReadRowValues(reader));
    }

    private static async IAsyncEnumerable<ChangeRow> ReadIncrementalAsync(
        DbConnection connection,
        SourceTableRef source,
        long previousVersion,
        long targetVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pkColumns = await MsSqlSchemaQueries.GetPrimaryKeyColumnsAsync(connection, source.Schema, source.Table, cancellationToken);
        if (pkColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table '{source.Schema}.{source.Table}' has no primary key; Change Tracking requires one.");

        var quotedSchema = SqlIdentifier.Quote(source.Schema);
        var quotedTable = SqlIdentifier.Quote(source.Table);
        var joinCondition = string.Join(" AND ", pkColumns.Select(pk => $"CT.{SqlIdentifier.Quote(pk)} = base.{SqlIdentifier.Quote(pk)}"));
        var pkSelectList = string.Join(", ", pkColumns.Select(pk => $"CT.{SqlIdentifier.Quote(pk)}"));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT CT.SYS_CHANGE_OPERATION, {pkSelectList}, base.*
            FROM CHANGETABLE(CHANGES {quotedSchema}.{quotedTable}, @previousVersion) AS CT
            LEFT JOIN {quotedSchema}.{quotedTable} AS base ON {joinCondition}
            WHERE CT.SYS_CHANGE_VERSION <= @targetVersion
            ORDER BY CT.SYS_CHANGE_VERSION;
            """;
        cmd.AddParameter("@previousVersion", previousVersion);
        cmd.AddParameter("@targetVersion", targetVersion);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var operation = reader.GetString(0) switch
            {
                "I" => ChangeOperation.Insert,
                "U" => ChangeOperation.Update,
                "D" => ChangeOperation.Delete,
                var op => throw new InvalidOperationException($"Unknown SYS_CHANGE_OPERATION '{op}'."),
            };

            var values = new Dictionary<string, object?>();
            for (var i = 1; i <= pkColumns.Count; i++)
                values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            // Deleted rows are gone from the base table — the LEFT JOIN yields NULLs for every
            // non-PK column, which would be indistinguishable from a real NULL value. Only PK
            // columns are reliable for deletes (see ChangeRow's XML doc).
            if (operation != ChangeOperation.Delete)
            {
                for (var i = pkColumns.Count + 1; i < reader.FieldCount; i++)
                    values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return new ChangeRow(operation, values);
        }
    }

    private static Dictionary<string, object?> ReadRowValues(DbDataReader reader)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
            values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }
}
