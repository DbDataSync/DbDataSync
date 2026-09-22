using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using MySqlConnector;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// MySQL and MariaDB, on MySqlConnector.
/// <para>
/// It registers nothing of its own beyond the trigger-audit DDL: every reader, staging provider and
/// writer here is <c>DbDataSync.Drivers.Generic</c>'s, driven by <see cref="MySqlDialect"/> — the same
/// claim <c>PostgresDriver</c>'s own doc comment makes, now proven a second time. The binlog-based
/// native alternative (<c>change-tracking-mysql.md</c>) is a later phase; nothing here is waiting on it.
/// </para>
/// </summary>
public sealed class MySqlDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => MySqlCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => MySqlDialect.Instance;

    public string DriverType => DriverIds.MySql;

    /// <summary>MySQL and MariaDB's shared default listening port.</summary>
    public int? DefaultPort => 3306;

    /// <summary>The <c>KnownLibraries</c> id this driver's own <c>MySqlDbType</c>/
    /// <c>MySqlConnectionStringBuilder</c> usage depends on — the same role
    /// <see cref="PostgresDriver.RequiredLibraryId"/>'s doc comment describes for <c>npgsql</c>.</summary>
    public string? RequiredLibraryId => "mysql-connector";

    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new WatermarkReader(MySqlDialect.Instance, MySqlValueBinding.Instance),
        new TriggerAuditReader(MySqlDialect.Instance, MySqlCatalog.Instance),
        new BatchReloadReader(MySqlDialect.Instance, MySqlValueBinding.Instance),
        new KeyReconcileReader(MySqlDialect.Instance, MySqlValueBinding.Instance),
    ];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
        [new BatchInsertStagingProvider(MySqlDialect.Instance, MySqlCatalog.Instance)];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
    [
        new DeleteInsertWriter(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance),
        new KeyReconcileDeleteWriter(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance),
        new KeyReconcileScd2CloseWriter(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance),
        new SnapshotWriter(MySqlDialect.Instance, MySqlCatalog.Instance),
        new Scd2Writer(MySqlDialect.Instance, MySqlCatalog.Instance),
    ];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        // An operator's own connection string is the base, not the whole truth: the credential goes on
        // top through this builder, so it is escaped correctly and config never carries it.
        var builder = string.IsNullOrWhiteSpace(connection.ConnectionString)
            ? new MySqlConnectionStringBuilder { Server = connection.Host, Database = connection.Database ?? "" }
            : new MySqlConnectionStringBuilder(connection.ConnectionString);

        if (!string.IsNullOrWhiteSpace(connection.Database))
            builder.Database = connection.Database;

        if (connection.Port is int port)
            builder.Port = (uint)port;

        if (connection.ConnectTimeoutSeconds is int connectTimeout)
            builder.ConnectionTimeout = (uint)connectTimeout;
        else if (!ConnectionTimeouts.AddressCarriesOwnConnectTimeout(connection, "Connection Timeout", "Connect Timeout"))
            builder.ConnectionTimeout = (uint)ConnectionTimeouts.DefaultConnectSeconds;

        if (connection.AuthMode == AuthMode.None)
        {
            // Whatever the address or the environment provides — a credential already in the connection
            // string, a server-side auth plugin configured to need nothing further. DbDataSync adds
            // nothing.
        }
        else if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            // MySQL has no equivalent of SQL Server's "integrated security" flag. The closest thing — an
            // auth plugin such as authentication_ldap_sasl or a PAM/Kerberos plugin — is server-side
            // configuration reached through Properties, not a driver-level switch; MySqlConnector still
            // needs a username either way.
            builder.UserID = connection.UserId
                ?? throw new InvalidOperationException("UserId is required even for IntegratedAuth on MySQL/MariaDB.");
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

        // Stamped here, at the one moment the config and the connection are in the same hand. Every
        // command raised through CreateTimedCommand() reads it back off the connection.
        return new MySqlConnection(builder.ConnectionString).WithCommandTimeout(connection);
    }

    /// <summary>
    /// The instance's user databases — <c>information_schema</c>, <c>mysql</c>, <c>performance_schema</c>
    /// and <c>sys</c> excluded, the same posture <c>PostgresDriver.ListDatabasesAsync</c> takes toward
    /// its own template databases: a source table is never going to live in one of the four, and listing
    /// them would only ever produce a failure downstream if picked.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY schema_name;
            """;

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        await MySqlDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await MySqlCatalog.Instance.ListTablesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await MySqlDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await MySqlCatalog.Instance.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>
    /// Reads <c>information_schema.tables.table_rows</c> — InnoDB's own background statistics estimate,
    /// no scan, and documented by InnoDB itself as approximate (off by a meaningful margin on a busy
    /// table). Null for a table the catalog cannot resolve, the same contract every other
    /// <see cref="ITableRowEstimator"/> already has.
    /// </summary>
    public async Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken)
    {
        await MySqlDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT table_rows FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table;";
        cmd.AddParameter("@schema", table.Schema);
        cmd.AddParameter("@table", table.Table);

        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Round-trips <c>VERSION()</c> — no user object, no permission beyond connecting.</summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT VERSION();";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), version, null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
        }
    }

    public IReadOnlyList<string> SupportedActions => MySqlProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        MySqlProvisioner.PlanAsync(connection, request, cancellationToken);
}
