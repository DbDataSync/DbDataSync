using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Oracle, on Oracle.ManagedDataAccess.Core (ODP.NET Core).
/// <para>
/// Every reader but one is <c>DbDataSync.Drivers.Generic</c>'s, driven by <see cref="OracleDialect"/> —
/// the same claim <c>PostgresDriver</c>'s and <c>MySqlDriver</c>'s own doc comments make.
/// <see cref="OracleFlashbackReader"/> is the exception: Flashback Version Query syntax belongs to
/// Oracle alone, so it is this phase's one genuinely new reader rather than a per-engine DDL class
/// plugged into an existing generic mechanism the way <see cref="OracleTriggerAudit"/> is.
/// </para>
/// </summary>
public sealed class OracleDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => OracleCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => OracleDialect.Instance;

    public string DriverType => DriverIds.Oracle;

    /// <summary>Oracle's default listening port.</summary>
    public int? DefaultPort => 1521;

    /// <summary>The <c>KnownLibraries</c> id this driver's own <c>OracleDbType</c>/
    /// <c>OracleConnectionStringBuilder</c> usage depends on — the same role
    /// <see cref="PostgresDriver.RequiredLibraryId"/>'s doc comment describes for <c>npgsql</c>.</summary>
    public string? RequiredLibraryId => "oracle-managed-data-access";

    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new WatermarkReader(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new TriggerAuditReader(OracleDialect.Instance, OracleCatalog.Instance),
        new BatchReloadReader(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new KeyReconcileReader(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new OracleFlashbackReader(OracleDialect.Instance),
    ];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
        [new BatchInsertStagingProvider(OracleDialect.Instance, OracleCatalog.Instance)];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
    [
        new DeleteInsertWriter(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new KeyReconcileDeleteWriter(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new KeyReconcileScd2CloseWriter(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance),
        new SnapshotWriter(OracleDialect.Instance, OracleCatalog.Instance),
        new Scd2Writer(OracleDialect.Instance, OracleCatalog.Instance),
    ];

    /// <summary>
    /// <c>OracleConnectionStringBuilder</c> has no <c>Host</c>/<c>Port</c>/<c>Database</c> properties at
    /// all — confirmed by reflection, not assumed: its only addressing surface is <c>DataSource</c>,
    /// which carries an EZConnect string, a TNS alias, or (already fully settled by phase 31) a wallet
    /// reference. Host-mode addressing is built here as an EZConnect string,
    /// <c>host:port/database</c> — <see cref="ConnectionConfig.Database"/> supplies the service name,
    /// resolving this phase's own open question about which of service-name-or-SID it means.
    /// </summary>
    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var builder = new OracleConnectionStringBuilder();

        if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            builder.ConnectionString = connection.ConnectionString;
        }
        else
        {
            var database = connection.Database ?? throw new InvalidOperationException(
                "Database (the Oracle service name) is required for host-mode addressing.");
            var port = connection.Port ?? 1521;
            builder.DataSource = $"{connection.Host}:{port}/{database}";
        }

        if (connection.ConnectTimeoutSeconds is int connectTimeout)
            builder.ConnectionTimeout = connectTimeout;
        else if (!ConnectionTimeouts.AddressCarriesOwnConnectTimeout(connection, "Connection Timeout"))
            builder.ConnectionTimeout = ConnectionTimeouts.DefaultConnectSeconds;

        if (connection.AuthMode == AuthMode.None)
        {
            // Whatever the address or the environment provides — a wallet (TnsAdmin-relative,
            // per-DataSource TNS alias), a credential already in the connection string. DbDataSync
            // adds nothing. Already fully settled by phase 31; nothing new here.
        }
        else if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            // Oracle.ManagedDataAccess.Core has no Windows Integrated Security support at all —
            // confirmed by reflection: OracleConnectionStringBuilder carries no such property, unlike
            // the classic Windows-only ODP.NET. There is no session-level equivalent to fall back to.
            throw new InvalidOperationException(
                "IntegratedAuth is not supported for Oracle by Oracle.ManagedDataAccess.Core (ODP.NET " +
                "Core has no Windows Integrated Security). Use AuthMode.None with a wallet, or SqlAuth.");
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

        return new OracleConnection(builder.ConnectionString).WithCommandTimeout(connection);
    }

    /// <summary>
    /// Oracle has no real multi-database-per-connection concept to browse — a connection is already
    /// bound to one service for its lifetime, the same reasoning <see cref="OracleDialect.UseDatabaseAsync"/>'s
    /// own doc comment gives. Returns the configured database (service name) as the sole entry, so a
    /// picker built for a three-level driver still has something to show rather than an empty list.
    /// </summary>
    public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([((OracleConnection)connection).ServiceName]);

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        OracleCatalog.Instance.ListTablesAsync(connection, cancellationToken);

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        OracleCatalog.Instance.GetColumnsAsync(connection, schema, table, cancellationToken);

    /// <summary>
    /// Reads <c>ALL_TABLES.NUM_ROWS</c> — populated by <c>DBMS_STATS</c>, no scan. Null for a table
    /// that has never been analysed, the same contract every other <see cref="ITableRowEstimator"/>
    /// implementation already has.
    /// </summary>
    public async Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT num_rows FROM all_tables WHERE owner = :schema AND table_name = :tableName";
        cmd.AddParameter("schema", table.Schema.ToUpperInvariant());
        cmd.AddParameter("tableName", table.Table.ToUpperInvariant());

        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Round-trips <c>SELECT 1 FROM DUAL</c> — no user object, no permission beyond connecting.</summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT banner FROM v$version WHERE banner LIKE 'Oracle%' FETCH FIRST 1 ROWS ONLY";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), version, null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
        }
    }

    public IReadOnlyList<string> SupportedActions => OracleProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        OracleProvisioner.PlanAsync(connection, request, cancellationToken);
}
