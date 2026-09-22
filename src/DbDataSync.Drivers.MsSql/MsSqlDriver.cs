using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

public sealed class MsSqlDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => MsSqlCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => MsSqlDialect.Instance;

    public string DriverType => DriverIds.MsSql;

    /// <summary>SQL Server's default listening port, pre-filled on a new connection. Declared here
    /// rather than in the SPA, which had a table of these that a third driver would have made stale.</summary>
    public int? DefaultPort => 1433;

    /// <summary>Phase 109j: the <c>KnownLibraries</c> id this driver's own <c>SqlBulkCopy</c>/
    /// <c>SqlDbType</c>/<c>SqlConnectionStringBuilder</c> usage depends on — the same id
    /// <see cref="DbDataSync.State.MsSqlStateDialect.LibraryId"/> and
    /// <c>DriverConnectionFactory.BuiltInDriverLibraryIds</c> already resolve this exact package
    /// through, restated here as a literal rather than a cross-project constant reference (this driver
    /// project does not otherwise depend on <c>DbDataSync.State</c>).</summary>
    public string? RequiredLibraryId => "microsoft-data-sqlclient";

    // The generic implementations are registered alongside this driver's own, not instead of them.
    // SQL Server's prefixed ones are faster (SqlBulkCopy, MERGE) and stay the default; the portable
    // ones are what proves the generic pipeline against a working engine, and are a real fallback on
    // an instance where bulk insert is not permitted.
    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new MsSqlChangeTrackingReader(),
        new MsSqlCdcReader(),
        new TriggerAuditReader(MsSqlDialect.Instance, MsSqlCatalog.Instance),
        new WatermarkReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
        new MsSqlBatchReloadReader(),
        new BatchReloadReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
        new KeyReconcileReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
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
        new KeyReconcileDeleteWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
        new KeyReconcileScd2CloseWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
            new SnapshotWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance),
        new Scd2Writer(MsSqlDialect.Instance, MsSqlCatalog.Instance),
    ];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        // An operator's own connection string is the *base*, not the whole truth: the credential is
        // applied on top through this builder, so it is escaped correctly rather than concatenated, and
        // config never has to carry it. Everything below then behaves identically in both modes.
        var builder = string.IsNullOrWhiteSpace(connection.ConnectionString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = connection.Port is int port ? $"{connection.Host},{port}" : connection.Host,
                InitialCatalog = connection.Database ?? "master",
            }
            : new SqlConnectionStringBuilder(connection.ConnectionString);

        if (!string.IsNullOrWhiteSpace(connection.Database))
            builder.InitialCatalog = connection.Database;

        ApplyDefaults(builder);

        // The setting wins when it is set, and defers to a connection string that already said
        // otherwise when it is not — the same rule ApplyDefaults follows above, for the same reason:
        // an operator who wrote `Connect Timeout=5` into their own connection string meant it, and
        // silently replacing it with our 30 would be worse than never having had a default.
        if (connection.ConnectTimeoutSeconds is int connectTimeout)
            builder.ConnectTimeout = connectTimeout;
        else if (!ConnectionTimeouts.AddressCarriesOwnConnectTimeout(
            connection, "Connect Timeout", "Connection Timeout", "ConnectTimeout"))
        {
            // All three spellings, because SqlClient accepts all three and an operator who used the
            // one we did not check would have their value silently replaced.
            builder.ConnectTimeout = ConnectionTimeouts.DefaultConnectSeconds;
        }

        if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else if (connection.AuthMode == AuthMode.SqlAuth)
        {
            builder.UserID = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            builder.Password = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        // AuthMode.None: whatever the address or the environment provides is used, and DbDataSync adds
        // nothing.

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        // Stamped here, at the one moment the config and the connection are in the same hand. Every
        // command raised through CreateTimedCommand() reads it back off the connection.
        return new SqlConnection(builder.ConnectionString).WithCommandTimeout(connection);
    }

    private static void ApplyDefaults(SqlConnectionStringBuilder builder)
    {
        var defaults = new SqlConnectionStringBuilder
        {
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

        // Only where the operator has not already said otherwise — a connection string that sets
        // Encrypt or turns MARS off meant it, and silently overriding it would be worse than the
        // default being absent.
        foreach (var key in new[] { nameof(SqlConnectionStringBuilder.TrustServerCertificate), nameof(SqlConnectionStringBuilder.MultipleActiveResultSets) })
        {
            if (!builder.ShouldSerialize(key))
                builder[key] = defaults[key];
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
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

        using var cmd = connection.CreateTimedCommand();
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
    /// Sums the row counts the engine already maintains per heap/clustered-index partition
    /// (<c>index_id IN (0, 1)</c>) — no scan, and no permission beyond seeing the table itself, since
    /// <c>sys.partitions</c> is visibility-scoped to objects the login can already see. Null when the
    /// table resolves to nothing.
    /// </summary>
    public async Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(table.Database);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT SUM(p.rows)
            FROM sys.partitions p
            JOIN sys.tables t   ON t.object_id = p.object_id
            JOIN sys.schemas s  ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table AND p.index_id IN (0, 1);
            """;
        cmd.AddParameter("@schema", table.Schema);
        cmd.AddParameter("@table", table.Table);

        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
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
            using var cmd = connection.CreateTimedCommand();
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