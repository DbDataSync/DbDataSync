using System.Data.Common;
using System.Diagnostics;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;

namespace DataSync.Drivers.MsSql;

public sealed class MsSqlDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => MsSqlCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => MsSqlDialect.Instance;

    public ConnectionDriverType DriverType => ConnectionDriverType.MsSql;

    // The generic implementations are registered alongside this driver's own, not instead of them.
    // SQL Server's prefixed ones are faster (SqlBulkCopy, MERGE) and stay the default; the portable
    // ones are what proves the generic pipeline against a working engine, and are a real fallback on
    // an instance where bulk insert is not permitted.
    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new MsSqlChangeTrackingReader(),
        new WatermarkReader(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
        new MsSqlBatchReloadReader(),
        new BatchReloadReader(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
    ];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
    [
        new MsSqlStagingTableProvider(),
        new BatchInsertStagingProvider(MsSqlDialect.Instance, MsSqlCatalog.Instance),
    ];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
    [
        new MsSqlMergeWriter(),
        new MsSqlMergeReconcileWriter(),
        new MsSqlDeleteInsertWriter(),
        new DeleteInsertWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
    ];

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

    /// <summary>
    /// Round-trips <c>SELECT @@VERSION</c>. Chosen because it touches no user object and needs no
    /// permission beyond connecting, so a successful test means "this login reaches this instance" and
    /// nothing more — which is exactly the question being asked.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION;";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(
                Succeeded: true,
                Stopwatch.GetElapsedTime(started),
                // One line: @@VERSION spans four, and the rest is build and OS detail that turns a
                // status card into a wall of text.
                version?.Split('\n')[0].Trim(),
                Error: null);
        }
        catch (DbException ex)
        {
            // An unhealthy instance is an answer to the question, not a fault to propagate.
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), ServerVersion: null, ex.Message);
        }
    }

    public IReadOnlyList<string> SupportedActions => MsSqlProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        MsSqlProvisioner.PlanAsync(connection, request, cancellationToken);
}