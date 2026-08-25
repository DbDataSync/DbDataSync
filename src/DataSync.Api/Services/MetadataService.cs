using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Api.Services;

/// <summary>
/// On-demand schema introspection for a configured connection (architecture/detailed-design.md §3.1)
/// — opens a connection, runs one metadata query, closes it. No data movement.
/// </summary>
public sealed class MetadataService(ConfigRepository configRepository, DriverRegistry driverRegistry, SecretStore secretStore)
{
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionName, CancellationToken cancellationToken)
    {
        var (connection, driver) = await OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListDatabasesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(string connectionName, string database, CancellationToken cancellationToken)
    {
        var (connection, driver) = await OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListTablesAsync(connection, database, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var (connection, driver) = await OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await driver.ListColumnsAsync(connection, database, schema, table, cancellationToken);
    }

    private async Task<(System.Data.Common.DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CancellationToken cancellationToken)
    {
        var config = configRepository.LoadConnection(connectionName);
        var driver = driverRegistry.Get(config.DriverType);
        var credential = config.AuthMode == AuthMode.SqlAuth
            ? secretStore.Resolve(config.CredentialSecretRef!)
            : null;

        var connection = driver.CreateConnection(config, credential);
        await connection.OpenAsync(cancellationToken);
        return (connection, driver);
    }
}
