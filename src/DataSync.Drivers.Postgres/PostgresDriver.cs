using System.Data.Common;
using System.Diagnostics;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Npgsql;

namespace DataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL, on Npgsql.
/// <para>
/// It registers nothing of its own: every reader, staging provider and writer here is
/// <c>DataSync.Drivers.Generic</c>'s, driven by <see cref="PostgresDialect"/>. That is the claim
/// phases 17 and 18 made — a new engine is a dialect, a connection factory and a catalog — and this
/// file is what it looks like when it holds. An engine-specific <c>COPY</c> staging provider is a
/// later phase; nothing here is waiting on it.
/// </para>
/// </summary>
public sealed class PostgresDriver : IDriver, IConnectionTester, IProvisioner
{
    public ConnectionDriverType DriverType => ConnectionDriverType.Postgres;

    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new WatermarkReader(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),
        new BatchReloadReader(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),
    ];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
        [new BatchInsertStagingProvider(PostgresDialect.Instance, PostgresCatalog.Instance)];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
        [new DeleteInsertWriter(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance)];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Database = connection.Database ?? "postgres",
        };

        if (connection.Port is int port)
            builder.Port = port;

        if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            // Kerberos/GSSAPI or peer auth, depending on the server's pg_hba.conf. Npgsql needs a
            // username either way; there is no equivalent of SQL Server's "integrated security" flag
            // that supplies one.
            builder.Username = connection.UserId
                ?? throw new InvalidOperationException("UserId is required even for IntegratedAuth on PostgreSQL.");
        }
        else
        {
            builder.Username = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            builder.Password = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        return new NpgsqlConnection(builder.ConnectionString);
    }

    /// <summary>
    /// <c>datistemplate = false</c> drops template0/template1; <c>datallowconn</c> drops anything the
    /// server will not accept a connection to, which would only ever produce a failure downstream.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true ORDER BY datname;";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        await PostgresDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await PostgresCatalog.Instance.ListTablesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await PostgresDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await PostgresCatalog.Instance.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>Round-trips <c>version()</c> — no user object, no permission beyond connecting.</summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version();";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), version?.Split('\n')[0].Trim(), null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
        }
    }

    public IReadOnlyList<string> SupportedActions => PostgresProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        PostgresProvisioner.PlanAsync(connection, request, cancellationToken);
}
