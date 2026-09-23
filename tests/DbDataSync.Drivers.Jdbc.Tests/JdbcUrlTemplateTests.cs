using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Jdbc.Ado;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 175M: <see cref="JdbcDriverSpec.UrlTemplate"/> placement-or-fallback, the no-URL-source config
/// error, and <see cref="JdbcProviderFactory.GetJdbcConnection"/>'s <c>acceptsURL</c> check. Loads the
/// same real pgJDBC driver/jar every other class in this project does.
/// <para>
/// **Not covered here**: <c>JdbcConnection.Open</c>'s soft <c>isValid</c> check (returns <c>false</c> →
/// fails; throws <c>SQLException</c> → doesn't fail). Both branches need a <c>java.sql.Connection</c>
/// that reports one of those outcomes on demand, and pgJDBC — the only driver this test project loads —
/// implements <c>isValid</c> normally and always reports a genuinely open connection as valid. Mocking
/// <c>java.sql.Connection</c> (a large interface, over IKVM interop) has no precedent anywhere in this
/// test project and isn't worth introducing for these two branches alone; every other test in this class
/// and <see cref="JdbcConnectionTests"/> does exercise the <c>isValid</c>-returns-<c>true</c> path on a
/// real connection without it breaking <c>Open()</c>, which is what these tests can actually prove.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcUrlTemplateTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>
{
    private static readonly string JarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");

    private static JdbcGenericDriver NewDriver(string id, string? urlTemplate = null) =>
        new(new JdbcDriverSpec(
            id, JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", [JarPath],
            Readers: [], Staging: [], Writers: [], UrlTemplate: urlTemplate));

    /// <summary>Every value the template references gets substituted, and — since this template has no
    /// <c>{username}</c> placeholder — the username still reaches the driver, as a property. Opened for
    /// real, not just assembled: proves the built URL is one pgJDBC actually accepts and connects with,
    /// not just a string that looks right.</summary>
    [Fact]
    public async Task CreateConnection_WithAFullyPlaceholderedTemplate_ProducesTheExpectedUrl_AndConnects()
    {
        var driver = NewDriver("jdbc-template-full", "jdbc:postgresql://{host}:{port}/{database}");
        var config = new ConnectionConfig
        {
            Name = "jdbc-template-full", DriverType = "Jdbc",
            Host = "localhost", Port = 15432, Database = db.DatabaseName,
            AuthMode = AuthMode.SqlAuth, UserId = "dbdatasync",
        };

        await using var connection = driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        var built = new JdbcConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal($"jdbc:postgresql://localhost:15432/{db.DatabaseName}", built.JdbcUrl);
        Assert.Equal("dbdatasync", built["user"]);
        Assert.Equal("DbDataSync_Test_Pw1", built["password"]);
        // host/port/database were all consumed by the template — none of them should also leak through
        // as a property under keys.Host/Port/Database's own name (the whole point of PlaceOrFallback).
        Assert.False(built.ContainsKey("host"));
        Assert.False(built.ContainsKey("port"));
        Assert.False(built.ContainsKey("database"));

        connection.Open();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>A template that doesn't reference <c>{database}</c> at all — the resolved value falls
    /// back to a <c>database</c> property instead of being silently dropped. Assembly-only (no
    /// <c>.Open()</c>): pgJDBC doesn't recognize an arbitrary <c>database</c> property key the way it
    /// recognizes <c>user</c>/<c>password</c> (those are real JDBC-standard property names; "database"
    /// here is just this codebase's own default key spelling), so proving the *fallback happened* is the
    /// point, not proving pgJDBC does something useful with it once it has.</summary>
    [Fact]
    public void CreateConnection_WithATemplateNotReferencingDatabase_FallsBackToADatabaseProperty()
    {
        var driver = NewDriver("jdbc-template-partial", "jdbc:postgresql://{host}:{port}/");
        var config = new ConnectionConfig
        {
            Name = "jdbc-template-partial", DriverType = "Jdbc",
            Host = "localhost", Port = 15432, Database = db.DatabaseName,
            AuthMode = AuthMode.None,
        };

        using var connection = driver.CreateConnection(config, credential: null);
        var built = new JdbcConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("jdbc:postgresql://localhost:15432/", built.JdbcUrl);
        Assert.Equal(db.DatabaseName, built["database"]);
    }

    /// <summary>No <c>ConnectionConfig.ConnectionString</c> and no <c>UrlTemplate</c> — nothing to build
    /// a JDBC URL from at all. A config error the operator needs to see plainly, not an NRE or a
    /// downstream "Connection is closed." three calls later.</summary>
    [Fact]
    public void CreateConnection_WithNeitherConnectionStringNorUrlTemplate_ThrowsNamingTheSpec()
    {
        var driver = NewDriver("jdbc-no-url-source");
        var config = new ConnectionConfig
        {
            Name = "jdbc-no-url-source", DriverType = "Jdbc", Host = "localhost",
            AuthMode = AuthMode.None,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => driver.CreateConnection(config, credential: null));
        Assert.Contains("jdbc-no-url-source", ex.Message);
    }

    /// <summary>The regression phase 175M's own "Bug that started this" section describes: before this
    /// fix, a URL the driver doesn't recognize produced a connection that opened "successfully" with a
    /// null underlying java.sql.Connection, then failed on the next command with a flatly misleading
    /// "Connection is closed." — no trace of the real problem. Now <c>acceptsURL</c> is checked first, so
    /// <c>Open()</c> itself fails, naming the actual rejected URL.</summary>
    [Fact]
    public void Open_WithAUrlTheDriverDoesNotAccept_ThrowsNamingTheUrl_InsteadOfOpeningSilently()
    {
        const string rejectedUrl = "jdbc:not-a-real-scheme://localhost/nope";
        var driver = NewDriver("jdbc-rejected-url");
        var config = new ConnectionConfig
        {
            Name = "jdbc-rejected-url", DriverType = "Jdbc", ConnectionString = rejectedUrl,
            AuthMode = AuthMode.None,
        };

        using var connection = driver.CreateConnection(config, credential: null);
        var ex = Assert.Throws<InvalidOperationException>(connection.Open);
        Assert.Contains(rejectedUrl, ex.Message);
    }
}
