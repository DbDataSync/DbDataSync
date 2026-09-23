using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Ado;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// Phase 165V's reader-only JDBC source driver, renamed and refactored onto <see cref="GenericDriverBase{TSpec}"/>
/// in phase 168V — <c>JdbcDriver</c> is what this class used to be called, before it could be built from
/// a <c>driver.yaml</c> descriptor the same way <see cref="GenericDriver"/> already could.
/// <para>
/// Parameterized by <see cref="JdbcDriverSpec.DriverClass"/>/<see cref="JdbcDriverSpec.DriverJarPaths"/>
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
/// (in <c>Ado/</c>) is what turns that generic <see cref="System.Data.DbType"/> into the right
/// <c>PreparedStatement.setXxx</c> call.
/// </para>
/// </summary>
public sealed class JdbcGenericDriver : GenericDriverBase<JdbcDriverSpec>, IConnectionPreviewer
{
    /// <summary>Phase 176M's masked placeholder for <see cref="PreviewConnection"/> — never the real
    /// secret, so a redaction bug downstream of this call can't leak it.</summary>
    private const string MaskedCredential = "••••••";


    public JdbcGenericDriver(JdbcDriverSpec spec)
        : base(spec, spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, new JdbcProviderFactoryHandle()))
    {
        // Phase 178N: eager, not discovered later as a silently-unsubstituted token in the assembled
        // URL — see follow-up-jdbc-url-template-password-placeholder-validation.md. Fires for both a
        // driver.yaml-built spec and a directly-constructed one (every existing test fixture); there is
        // no path to a working JdbcGenericDriver that skips this constructor.
        if (spec.UrlTemplate?.Contains("{password}") == true)
        {
            throw new NotSupportedException(
                $"'{spec.Id}': UrlTemplate contains a {{password}} placeholder, which is never substituted " +
                "— a credential is always sent as a JDBC property, never placed in the URL. Remove the " +
                "placeholder; the password is added automatically.");
        }
        JdbcProviderFactory.FromJarPaths(spec.DriverJarPaths, spec.DriverClass);
    }

    /// <summary>The <c>KnownLibraries</c> id this driver's own IKVM/<c>java.sql.*</c> usage depends on —
    /// the same role <c>PostgresDriver.RequiredLibraryId</c>'s doc comment describes for <c>npgsql</c>.
    /// Resolved from <c>libraries/ikvm/lib/</c> at runtime via <c>LibraryRegistry</c>, exactly like every
    /// other compiled driver's client library — see this project's own csproj comment for what IKVM's
    /// own packaging needed excluded to make that true. Hardcoded, not descriptor-driven: every
    /// JDBC-via-IKVM engine needs IKVM itself, regardless of which vendor's jar it loads.</summary>
    public string? RequiredLibraryId => "ikvm";

    /// <summary>Phase 175M's default key spellings when a spec doesn't supply its own
    /// <see cref="JdbcDriverSpec.ConnectionStringKeys"/> — the literal <c>java.util.Properties</c> names
    /// a real JDBC driver reads, not <see cref="GenericConnectionStringKeys"/>'s own ADO.NET-flavoured
    /// defaults (<c>Host</c>/<c>User Id</c>/…), which would be the wrong spelling here.</summary>
    public static readonly GenericConnectionStringKeys DefaultConnectionStringKeys =
        new(Host: "host", Port: "port", Database: "database", Username: "user", Password: "password");

    /// <summary>Real credential from <see cref="ConnectionConfig"/>'s own resolved secret — see
    /// <see cref="BuildUnifiedJdbcUrlAndProperties"/> for how it's assembled.</summary>
    public override DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var (jdbcUrl, props) = BuildUnifiedJdbcUrlAndProperties(connection, credential);
        var connectionString = JdbcConnectionStringBuilder.CreateConnectionString(Spec.DriverClass, jdbcUrl, props);
        return new JdbcConnection { ConnectionString = connectionString }.WithCommandTimeout(connection);
    }

    /// <summary>Phase 176M. <see cref="MaskedCredential"/> stands in for the real credential — never
    /// touches the network, never the real secret. Same assembly as <see cref="CreateConnection"/>, via
    /// <see cref="BuildUnifiedJdbcUrlAndProperties"/>, so this can't drift from what a real connection
    /// would actually resolve to.</summary>
    public ConnectionPreview PreviewConnection(ConnectionConfig connection)
    {
        var (jdbcUrl, props) = BuildUnifiedJdbcUrlAndProperties(connection, MaskedCredential);
        var connectionString = JdbcConnectionStringBuilder.CreateConnectionString(Spec.DriverClass, jdbcUrl, props);
        var propertyNames = (object[]?)props.stringPropertyNames()?.toArray() ?? [];
        var properties = propertyNames.Cast<string>().ToDictionary(name => name, name => props.getProperty(name));
        return new ConnectionPreview(connectionString, jdbcUrl, properties);
    }

    /// <summary>
    /// Phase 175M/176M. Unifies like <see cref="GenericDriver.CreateConnection"/> does — one scratch
    /// <see cref="DbConnectionStringBuilder"/>, <see cref="ConnectionConfig.Host"/>/
    /// <see cref="ConnectionConfig.Database"/>/<see cref="ConnectionConfig.Port"/>/the
    /// <see cref="AuthMode"/> branch, all via <see cref="JdbcDriverSpec.ConnectionStringKeys"/> — before
    /// anything JDBC-specific happens (unlike <see cref="GenericDriver"/>, this deliberately skips
    /// seeding the scratch builder from <see cref="ConnectionConfig.ConnectionString"/>/its own connect-
    /// timeout unification — see the two comments inline for why, both specific to JDBC's shape). Only
    /// once that's fully populated are host/port/database/username pulled back out **by key name**,
    /// never by parsing a JDBC URL and never read straight off <c>connection.Host</c>/<c>Port</c> (those
    /// may be unset under connection-string addressing).
    /// <para>
    /// Each of those four can be placed into <see cref="JdbcDriverSpec.UrlTemplate"/>, or falls back to a
    /// JDBC property under its own key if the template doesn't reference it — a resolved value is never
    /// silently discarded because a template happened not to mention it. <c>Password</c> is the one
    /// exception: always a property, never template-eligible, matching the "credential never in the URL"
    /// rule <see cref="ConnectionConfig.ConnectionString"/>'s own doc comment documents.
    /// </para>
    /// <para>
    /// A hand-pasted, complete JDBC URL (the pre-175M contract) has no <c>{placeholder}</c> tokens in it
    /// at all, so every resolved value falls back to a property automatically — the same outcome as
    /// today for an operator who already types the whole URL, not a behavior change for them.
    /// </para>
    /// <para>
    /// Shared between <see cref="CreateConnection"/> (real credential) and <see cref="PreviewConnection"/>
    /// (<see cref="MaskedCredential"/>) — phase 176M's own reason to factor this out, one assembly path,
    /// two callers, no duplicated key-mapping logic.
    /// </para>
    /// </summary>
    private (string JdbcUrl, java.util.Properties Properties) BuildUnifiedJdbcUrlAndProperties(
        ConnectionConfig connection, string? credential)
    {
        var keys = Spec.ConnectionStringKeys ?? DefaultConnectionStringKeys;

        // Unlike GenericDriver's own ConnectionString (a real ADO.NET key=value string an operator can
        // seed the builder from), a JDBC ConnectionString is the URL itself — not that shape at all, and
        // never fed into this scratch builder. It's used below, directly, as urlText. Connect-timeout
        // unification is skipped for the same reason: GenericDriver's own
        // ConnectionTimeouts.AddressCarriesOwnConnectTimeout parses ConnectionString as ADO.NET
        // key=value pairs to check whether the operator already set one there — meaningless (and liable
        // to misparse) against a JDBC URL, and nothing below ever reads a resolved connect-timeout back
        // out anyway, so there is nothing here worth mirroring.
        var unified = new DbConnectionStringBuilder();
        unified[keys.Host] = connection.Host;
        unified[keys.Database] = connection.Database;

        if (connection.Port is int port && keys.Port is not null)
            unified[keys.Port] = port;

        string? password = null;
        if (connection.AuthMode == AuthMode.None)
        {
            // Whatever the URL/template or the environment provides. DbDataSync adds nothing.
        }
        else if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            if (keys.IntegratedSecurity is not null)
            {
                unified[keys.IntegratedSecurity] = true;
            }
            else
            {
                unified[keys.Username] = connection.UserId
                    ?? throw new InvalidOperationException($"UserId is required even for IntegratedAuth on '{Spec.Id}'.");
            }
        }
        else
        {
            unified[keys.Username] = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            password = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        string? Resolved(string key) => unified.ContainsKey(key) ? Convert.ToString(unified[key]) : null;
        var host = Resolved(keys.Host);
        var portText = keys.Port is not null ? Resolved(keys.Port) : null;
        var database = Resolved(keys.Database);
        var username = Resolved(keys.Username);

        var urlText = connection.ConnectionString ?? Spec.UrlTemplate
            ?? throw new InvalidOperationException(
                $"'{Spec.Id}': no ConnectionString and no UrlTemplate — nothing to build a JDBC URL from.");

        var props = new java.util.Properties();
        string PlaceOrFallback(string url, string placeholder, string? value, string propertyKey)
        {
            if (value is null) return url;
            var token = "{" + placeholder + "}";
            if (url.Contains(token)) return url.Replace(token, value);
            props.setProperty(propertyKey, value);
            return url;
        }

        var jdbcUrl = urlText;
        jdbcUrl = PlaceOrFallback(jdbcUrl, "host", host, keys.Host);
        if (keys.Port is not null) jdbcUrl = PlaceOrFallback(jdbcUrl, "port", portText, keys.Port);
        jdbcUrl = PlaceOrFallback(jdbcUrl, "database", database, keys.Database);
        jdbcUrl = PlaceOrFallback(jdbcUrl, "username", username, keys.Username);
        if (password is not null) props.setProperty(keys.Password, password);

        // The arbitrary-property passthrough this driver already had before 175M — a JDBC-specific
        // tuning flag or SSL setting an operator set directly, orthogonal to host/port/database/username
        // unification above. Applied last, so it can't be silently overridden by anything derived above
        // (nothing above writes these same keys unless an operator's own key collides with one of
        // keys.Host/Port/Database/Username/Password, in which case this — the operator's own explicit,
        // most specific setting — wins, same precedence GenericDriver's own Properties pass keeps).
        foreach (var (key, value) in connection.Properties)
            props.setProperty(key, value);

        return (jdbcUrl, props);
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
    /// <para>
    /// Phase 169V: <paramref name="repoRoot"/> is what lets this resolve <c>jdbc.driverJarPaths</c> —
    /// names inside <c>&lt;repo&gt;/files/</c>, not filesystem paths, see that field's own doc comment —
    /// into the real, resolved paths <see cref="JdbcDriverSpec.DriverJarPaths"/> carries. The same
    /// "resolve at the boundary, carry a real value from there on" shape <c>DriverDescriptorReader.ToSpec</c>'s
    /// <c>library:</c> → <see cref="DbProviderFactory"/> resolution already uses for the ADO.NET path —
    /// this is <c>BuildDriver</c>'s reflection convention's own second parameter (see its own doc comment),
    /// not an ambient lookup.
    /// </para>
    /// </summary>
    public static IDriver FromDescriptor(DriverDescriptorYaml descriptor, string repoRoot)
    {
        var jdbc = descriptor.Jdbc
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base 'JdbcGenericDriver' requires a jdbc block (driverClass, driverJarPaths).");

        // Forced, not read as an operator setting — see DescriptorDialectYaml.ParameterNameIsBare's own
        // doc comment. JdbcCommand's @name -> ordinal-? translation always needs the bare name; there is
        // no vendor or descriptor for which false is correct here, so this is never left to a driver.yaml
        // to get right (or silently get wrong until the first segmented bulk load binds a parameter).
        descriptor.Dialect.ParameterNameIsBare = true;
        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
        var catalog = DescriptorCatalogResolution.Resolve(
            descriptor.Id, descriptor.Dialect.Catalog, descriptor.MetadataQueries?.TableQuery,
            descriptor.MetadataQueries?.ColumnQuery, @default: JdbcCatalog.Instance);

        var jarPaths = jdbc.DriverJarPaths.Select(name => FilesPaths.FilePath(repoRoot, name)).ToList();

        // Per-field fallback to DefaultConnectionStringKeys, not "block present or not" — a yaml
        // overriding only one field (username, say — exactly what a single-field edit through the web
        // console's own connection-string-keys form writes) must not silently apply an ADO.NET-flavoured
        // spelling to the other five just because JdbcConnectionStringKeysYaml's own type is now
        // involved. See that type's own doc comment for the bug this replaced (found via a validate-
        // endpoint test asserting the *unmodified* fields, not assumed).
        var keys = jdbc.ConnectionStringKeys is { } k
            ? new GenericConnectionStringKeys(
                k.Host ?? DefaultConnectionStringKeys.Host,
                k.Port ?? DefaultConnectionStringKeys.Port,
                k.Database ?? DefaultConnectionStringKeys.Database,
                k.Username ?? DefaultConnectionStringKeys.Username,
                k.Password ?? DefaultConnectionStringKeys.Password,
                k.ConnectTimeout ?? DefaultConnectionStringKeys.ConnectTimeout,
                k.IntegratedSecurity ?? DefaultConnectionStringKeys.IntegratedSecurity)
            : null;

        return new JdbcGenericDriver(new JdbcDriverSpec(
            descriptor.Id,
            dialect,
            catalog,
            jdbc.DriverClass,
            jarPaths,
            Readers: descriptor.Capabilities.Readers,
            Staging: descriptor.Capabilities.Staging,
            Writers: descriptor.Capabilities.Writers,
            UrlTemplate: jdbc.UrlTemplate,
            ConnectionStringKeys: keys,
            DisplayName: descriptor.DisplayName,
            TestQuery: descriptor.TestQuery));
    }
}

/// <summary>
/// <see cref="GenericValueBinder"/> needs a <see cref="DbProviderFactory"/> only to call
/// <see cref="DbProviderFactory.CreateParameter"/> — this is that, without exposing the real
/// <c>JdbcProviderFactory</c> (which is <c>internal</c> to <c>Ado/</c> and keyed by driver class,
/// not a singleton) outside this project.
/// </summary>
internal sealed class JdbcProviderFactoryHandle : DbProviderFactory
{
    public override DbParameter CreateParameter() => new JdbcParameter();
}
