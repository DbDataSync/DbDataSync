using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 169V's own "how to verify" section, built: a driver class resolved from a classpath spanning
/// more than one real jar, not just a one-element list wearing a plural name. The section's first-choice
/// fixture — two small jars with a real class-to-class dependency between them — needs <c>javac</c>/
/// <c>jar</c>, unavailable in this environment; its own named fallback is used instead: a second,
/// genuinely distinct real jar (<c>commons-logging</c>, contributing nothing pgJDBC needs) listed
/// alongside pgJDBC's own jar, proving <see cref="Imported.JdbcProviderFactory.FromJarPaths"/>'s combined
/// classpath actually spans multiple files rather than merely tolerating a list of one repeated entry.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcMultipleJarsTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private JdbcGenericDriver _driver = null!;
    private System.Data.Common.DbConnection _connection = null!;

    public Task InitializeAsync()
    {
        var postgresqlJar = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        var decoyJar = Path.Combine(AppContext.BaseDirectory, "decoy.jar");

        // Decoy listed first — if FromJarPaths silently only used the first URL (the bug this test would
        // catch), the driver class would fail to resolve at all rather than resolve from the wrong jar.
        _driver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-multi-jar-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver",
            [decoyJar, postgresqlJar], Readers: [], Staging: [], Writers: []));

        var config = new ConnectionConfig
        {
            Name = "jdbc-multi-jar-test",
            DriverType = "Jdbc",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        _connection = _driver.CreateConnection(config, "DbDataSync_Test_Pw1");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public void TheDriverClass_ResolvesFromACombinedClasspathSpanningTwoRealJars()
    {
        // Open() is exactly where Class.forName(driverClass, true, classLoader) runs (via
        // JdbcProviderFactory.FromClassLoader) — reaching a live, working connection is the proof the
        // combined classpath (not just the first URL in the array) was actually searched.
        _connection.Open();
        Assert.Equal(System.Data.ConnectionState.Open, _connection.State);
    }
}
