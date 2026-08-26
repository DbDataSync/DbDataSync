using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;

namespace DataSync.Drivers.MsSql;

public sealed class MsSqlDriver : IDriver
{
    public ConnectionDriverType DriverType => ConnectionDriverType.MsSql;

    public IReadOnlyList<IChangeReader> Readers { get; } =
        [new MsSqlChangeTrackingReader(), new MsSqlWatermarkReader(), new MsSqlBatchReloadReader()];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
        [new MsSqlStagingTableProvider()];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
        [new MsSqlMergeWriter(), new MsSqlMergeReconcileWriter(), new MsSqlDeleteInsertWriter()];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Port is int port ? $"{connection.Host},{port}" : connection.Host,
            InitialCatalog = connection.Database ?? "master",
            // Dev/test default for connecting to self-signed instances (e.g. the local Docker
            // container used in Phase 3 testing). Revisit before any hardened-production deployment
            // guidance ships — see architecture/implementation/done/phase-003-mssql-driver.md.
            TrustServerCertificate = true,
            // Required: a source read (an open, streaming SqlDataReader from CHANGETABLE/a batch
            // query) and a staging SqlBulkCopy/MERGE against the target can both be in flight on one
            // connection at once when source and target share a server. Without MARS that combination
            // deadlocks rather than throwing — see architecture/implementation/done/phase-003-mssql-driver.md.
            MultipleActiveResultSets = true,
        };

        if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            builder.Password = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        return new SqlConnection(builder.ConnectionString);
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        // database_id > 4 skips the fixed system databases (master, tempdb, model, msdb).
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name;";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.name, t.name
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            ORDER BY s.name, t.name;
            """;

        var results = new List<TableMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TableMetadata(reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);
        return await MsSqlSchemaQueries.GetColumnsAsync(connection, schema, table, cancellationToken);
    }
}
