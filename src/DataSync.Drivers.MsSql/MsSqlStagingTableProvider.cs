using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Stages a change set into a session-scoped local temp table (<c>#Staging_...</c>) via
/// <see cref="SqlBulkCopy"/>, populated column-by-column according to <see cref="ColumnMapping"/>
/// plus a trailing "__Operation" marker column consumed by <see cref="MsSqlMergeWriter"/>.
/// <para>
/// Local temp tables are scoped to the connection/session, so the caller must pass the *same* open
/// <see cref="DbConnection"/> to <see cref="MsSqlMergeWriter.ApplyAsync"/> that was used here — the
/// staging table would otherwise not exist. This mirrors how DataSync.TaskRunner is expected to use
/// one connection per target throughout a run (Phase 4).
/// </para>
/// </summary>
public sealed class MsSqlStagingTableProvider : IStagingProvider
{
    private const string OperationColumn = "__Operation";

    public string Kind => MsSqlDriverKinds.StagingTable;

    public async Task<StagedChangeSet> StageAsync(
        DbConnection targetConnection,
        TableRef target,
        IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (columnMappings.Count == 0)
            throw new InvalidOperationException("ColumnMappings must be specified to stage changes.");

        targetConnection.ChangeDatabase(target.Database);

        var targetColumnTypes = await MsSqlSchemaQueries.GetColumnsAsync(targetConnection, target.Schema, target.Table, cancellationToken);
        var typeByName = targetColumnTypes.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        foreach (var column in mappedTargetColumns)
        {
            if (!typeByName.ContainsKey(column))
                throw new InvalidOperationException(
                    $"Target column '{column}' was not found on '{target.Schema}.{target.Table}'.");
        }

        var stagingTable = $"#Staging_{Guid.NewGuid():N}";
        var columnDefs = string.Join(", ", mappedTargetColumns.Select(c => $"{SqlIdentifier.Quote(c)} {typeByName[c]} NULL"));

        using (var createCmd = targetConnection.CreateCommand())
        {
            createCmd.CommandText = $"CREATE TABLE {stagingTable} ({columnDefs}, {OperationColumn} CHAR(1) NOT NULL);";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var rowCount = await BulkCopyAsync(targetConnection, stagingTable, mappedTargetColumns, columnMappings, rows, cancellationToken);

        return new StagedChangeSet(stagingTable, rowCount);
    }

    private static async Task<long> BulkCopyAsync(
        DbConnection targetConnection,
        string stagingTable,
        IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyList<ColumnMapping> columnMappings,
        IAsyncEnumerable<ChangeRow> rows,
        CancellationToken cancellationToken)
    {
        var sourceColumnByTarget = columnMappings.ToDictionary(m => m.TargetColumn, m => m.SourceColumn);

        using var bulkCopy = new SqlBulkCopy((SqlConnection)targetConnection)
        {
            DestinationTableName = stagingTable,
        };
        foreach (var column in mappedTargetColumns)
            bulkCopy.ColumnMappings.Add(column, column);
        bulkCopy.ColumnMappings.Add(OperationColumn, OperationColumn);

        await using var dataReader = new ChangeRowDataReader(rows, mappedTargetColumns, sourceColumnByTarget, cancellationToken);
        await bulkCopy.WriteToServerAsync(dataReader, cancellationToken);
        return dataReader.RowsProduced;
    }
}
