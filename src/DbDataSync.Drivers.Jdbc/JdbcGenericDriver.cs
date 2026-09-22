using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Imported;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// Phase 165V's reader-only JDBC source driver, renamed and refactored onto <see cref="GenericDriverBase{TSpec}"/>
/// in phase 168V — <c>JdbcDriver</c> is what this class used to be called, before it could be built from
/// a <c>driver.yaml</c> descriptor the same way <see cref="GenericDriver"/> already could.
/// <para>
/// Parameterized by <see cref="JdbcDriverSpec.DriverClass"/>/<see cref="JdbcDriverSpec.DriverJarPath"/>
/// rather than hardcoding an engine — still exercised only from
/// <c>DbDataSync.Drivers.Jdbc.Tests</c> directly, or from a <c>driver.yaml</c> that names this class as
/// its <c>base</c>; still not in <c>BuiltInDrivers</c> (it takes a per-vendor driver class and jar, not a
/// parameterless constructor, so it could not be even if it were otherwise ready).
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
public sealed class JdbcGenericDriver : GenericDriverBase<JdbcDriverSpec>
{
    public JdbcGenericDriver(JdbcDriverSpec spec)
        : base(spec, spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, new JdbcProviderFactoryHandle()))
    {
        JdbcProviderFactory.FromJarPath(spec.DriverJarPath, spec.DriverClass);
    }

    /// <summary>The <c>KnownLibraries</c> id this driver's own IKVM/<c>java.sql.*</c> usage depends on —
    /// the same role <c>PostgresDriver.RequiredLibraryId</c>'s doc comment describes for <c>npgsql</c>.
    /// Resolved from <c>libraries/ikvm/lib/</c> at runtime via <c>LibraryRegistry</c>, exactly like every
    /// other compiled driver's client library — see this project's own csproj comment for what IKVM's
    /// own packaging needed excluded to make that true. Hardcoded, not descriptor-driven: every
    /// JDBC-via-IKVM engine needs IKVM itself, regardless of which vendor's jar it loads.</summary>
    public string? RequiredLibraryId => "ikvm";

    public override DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var jdbcUrl = connection.ConnectionString
            ?? throw new InvalidOperationException(
                "JdbcGenericDriver requires ConnectionConfig.ConnectionString to carry the JDBC URL " +
                "(AddressMode.connectionString) — Host/Port addressing has no URL template for this driver.");

        var builder = new JdbcConnectionStringBuilder { JdbcDriver = Spec.DriverClass, JdbcUrl = jdbcUrl };

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

    public override Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        // A JDBC connection here is bound to one database for its lifetime, the same posture
        // PostgresDialect.UseDatabaseAsync already takes — see that class's own doc comment.
        Task.FromResult<IReadOnlyList<string>>([connection.Database]);

    /// <summary>A no-op: a JDBC connection is bound to one database for its lifetime (see
    /// <see cref="ListDatabasesAsync"/>), so there is nothing to switch — unlike <see cref="GenericDriver"/>'s
    /// override, which asks <see cref="SqlDialect.UseDatabaseAsync"/>.</summary>
    protected override Task SwitchDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Built from a <c>driver.yaml</c> whose <c>base</c> names this class — phase 168V. Reads
    /// <see cref="DriverDescriptorYaml.Jdbc"/> for the driver class/jar (there is no ADO.NET
    /// <see cref="DbProviderFactory"/> to resolve through <see cref="DriverDescriptorYaml.Library"/>),
    /// and the same <see cref="DescriptorDialect"/>/<c>typeMap</c> a <see cref="GenericDriver"/> descriptor
    /// uses — including <see cref="DescriptorDialectYaml.ParameterNameIsBare"/>, which a JDBC descriptor
    /// must set <c>true</c> (see that field's own doc comment) or repeat phase 165V's own Finding 1.
    /// <see cref="DescriptorCatalogResolution"/> resolves <c>catalog: query</c> exactly as
    /// <c>GenericDriver.FromDescriptor</c>'s does; the default when omitted is <see cref="JdbcCatalog.Instance"/>
    /// (<c>java.sql.DatabaseMetaData</c>), not <c>information_schema</c>.
    /// </summary>
    public static IDriver FromDescriptor(DriverDescriptorYaml descriptor)
    {
        var jdbc = descriptor.Jdbc
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base 'JdbcGenericDriver' requires a jdbc block (driverClass, driverJarPath).");

        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
        var catalog = DescriptorCatalogResolution.Resolve(
            descriptor.Id, descriptor.Dialect.Catalog, descriptor.MetadataQueries?.TableQuery,
            descriptor.MetadataQueries?.ColumnQuery, @default: JdbcCatalog.Instance);

        return new JdbcGenericDriver(new JdbcDriverSpec(
            descriptor.Id,
            dialect,
            catalog,
            jdbc.DriverClass,
            jdbc.DriverJarPath,
            Readers: descriptor.Capabilities.Readers,
            Staging: descriptor.Capabilities.Staging,
            Writers: descriptor.Capabilities.Writers,
            DisplayName: descriptor.DisplayName));
    }
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
