using DbDataSync.Drivers.Descriptor;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="TestApiFactory"/> whose repo root already has a JDBC-backed <c>driver.yaml</c> — with
/// its own <c>testQuery</c> set — and the real pgJDBC jar it names, before the host ever starts. Built
/// to reproduce a reported bug end to end, through the exact path the other descriptor-driver factories
/// use (<see cref="DescriptorDriverApiFactory"/>'s own doc comment): real <c>DriverLoader.LoadDescriptorDrivers</c>
/// at real host startup, not a directly-constructed driver or a standalone descriptor parse — those
/// both already passed in unit-level tests (<c>JdbcConnectionTests</c>/<c>JdbcDescriptorTests</c>) and
/// proved nothing about whatever happens once the API host itself loads and registers this driver.
/// </summary>
public sealed class JdbcTestQueryApiFactory : TestApiFactory
{
    public const string DriverId = "postgres-via-jdbc-testquery-api";
    public const string TestQueryText = "SELECT 1::int AS one, current_database() AS db";

    public JdbcTestQueryApiFactory()
    {
        var filesDir = Path.Combine(RepoRoot, "files");
        Directory.CreateDirectory(filesDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "postgresql.jar"), Path.Combine(filesDir, "postgresql.jar"));

        var driverDir = Path.Combine(RepoRoot, "drivers", DriverId);
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), DriverYaml);
    }

    private static readonly string DriverYaml = $$"""
        id: {{DriverId}}
        displayName: Postgres (via JDBC, test-query repro)
        library: ikvm
        base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
        jdbc:
          driverClass: org.postgresql.Driver
          driverJarPaths: [postgresql.jar]
        testQuery: {{TestQueryText}}
        dialect:
          quoteIdentifier: doubleQuote
          parameterPrefix: "@"
          parameterNameIsBare: true
          rowLimit: limitOffset
        typeMap:
          int4: Int32
        capabilities:
          readers: []
          staging: []
          writers: []
        """;
}
