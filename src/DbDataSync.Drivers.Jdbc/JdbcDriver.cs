using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Imported;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// Phase 165V's reader-only JDBC source driver — the spike itself, not a shippable engine driver. See
/// <c>architecture/implementation/todo/phase-165V-jdbc-reader-spike-ikvm-postgres.md</c> for what this
/// deliberately does and does not build.
/// <para>
/// Unlike <c>PostgresDriver</c>/<c>MySqlDriver</c>, this one is parameterized by <paramref
/// name="driverClass"/> and <paramref name="driverJarPath"/> rather than hardcoding an engine — it is
/// exercised only from <c>DbDataSync.Drivers.Jdbc.Tests</c>, never registered in
/// <c>BuiltInDrivers</c>/<c>DbDataSyncHost</c>/<c>TaskRunner</c>'s composition roots. The jar this phase
/// tests against is pgJDBC (see the phase doc's "Confirmed by a real probe"), but nothing here assumes
/// Postgres beyond <see cref="JdbcCatalog"/>'s own choice to reuse <c>InformationSchemaQueries</c>.
/// </para>
/// <para>
/// Reuses <see cref="GenericValueBinder"/> rather than a driver-specific <c>ISegmentValueBinder</c> —
/// unlike Postgres/MySQL/Oracle, whose native parameter types (<c>NpgsqlDbType</c>, <c>MySqlDbType</c>)
/// are a real reason to write one, a JDBC parameter's "native" type *is* the generic <see
/// cref="System.Data.DbType"/>/<see cref="CanonicalType"/> pair <c>GenericValueBinder</c> already binds
/// through — there is no richer JDBC-specific enum to lose precision against. <see cref="JdbcCommand"/>
/// (in <c>Imported/</c>) is what turns that generic <see cref="System.Data.DbType"/> into the right
/// <c>PreparedStatement.setXxx</c> call.
/// </para>
/// </summary>
public sealed class JdbcDriver : IDriver
{
    private readonly string _driverClass;

    public JdbcDriver(string driverClass, string driverJarPath)
    {
        _driverClass = driverClass;
        JdbcProviderFactory.FromJarPath(driverJarPath, driverClass);
    }

    public string DriverType => "Jdbc";

    /// <summary>The <c>KnownLibraries</c> id this driver's own IKVM/<c>java.sql.*</c> usage depends on —
    /// the same role <c>PostgresDriver.RequiredLibraryId</c>'s doc comment describes for <c>npgsql</c>.
    /// Resolved from <c>libraries/ikvm/lib/</c> at runtime via <c>LibraryRegistry</c>, exactly like every
    /// other compiled driver's client library — see this project's own csproj comment for what IKVM's
    /// own packaging needed excluded to make that true.</summary>
    public string? RequiredLibraryId => "ikvm";

    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new WatermarkReader(JdbcDialect.Instance, new GenericValueBinder(JdbcDialect.Instance, new JdbcProviderFactoryHandle())),
        new BatchReloadReader(JdbcDialect.Instance, new GenericValueBinder(JdbcDialect.Instance, new JdbcProviderFactoryHandle())),
    ];

    // No TriggerAuditReader (no shadow-table DDL for this path), no writers, no staging providers, no
    // IProvisioner — reader-only, per the phase doc.
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
    public IReadOnlyList<IChangeWriter> Writers { get; } = [];

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var jdbcUrl = connection.ConnectionString
            ?? throw new InvalidOperationException(
                "JdbcDriver requires ConnectionConfig.ConnectionString to carry the JDBC URL " +
                "(AddressMode.connectionString) — Host/Port addressing has no URL template for this spike.");

        var builder = new JdbcConnectionStringBuilder { JdbcDriver = _driverClass, JdbcUrl = jdbcUrl };

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        if (connection.AuthMode == AuthMode.SqlAuth)
        {
            builder["user"] = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            // Never in the URL — see ConnectionConfig.ConnectionString's own doc comment and the
            // planning doc's connection-model table.
            builder["password"] = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        return new JdbcConnection { ConnectionString = builder.ConnectionString }.WithCommandTimeout(connection);
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        // A JDBC connection here is bound to one database for its lifetime, the same posture
        // PostgresDialect.UseDatabaseAsync already takes — see that class's own doc comment.
        Task.FromResult<IReadOnlyList<string>>([connection.Database]);

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        JdbcCatalog.Instance.ListTablesAsync(connection, cancellationToken);

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        JdbcCatalog.Instance.GetColumnsAsync(connection, schema, table, cancellationToken);
}

/// <summary>
/// <see cref="GenericValueBinder"/> needs a <see cref="DbProviderFactory"/> only to call
/// <see cref="DbProviderFactory.CreateParameter"/> — this is that, without exposing the real
/// <c>JdbcProviderFactory</c> (which is <c>internal</c> to <c>Imported/</c> and keyed by driver class,
/// not a singleton) outside this project.
/// </summary>
internal sealed class JdbcProviderFactoryHandle : DbProviderFactory
{
    public override DbParameter CreateParameter() => new JdbcParameter();
}
