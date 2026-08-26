using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Services;

/// <summary>
/// On-demand schema introspection for a configured connection (architecture/detailed-design.md §3.1)
/// — opens a connection, runs one metadata query, closes it. No data movement.
/// </summary>
public sealed class MetadataService(DriverConnectionFactory connections)
{
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionName, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListDatabasesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(string connectionName, string database, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListTablesAsync(connection, database, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListColumnsAsync(connection, database, schema, table, cancellationToken);
    }
}
