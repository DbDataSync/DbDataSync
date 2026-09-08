using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;

namespace DbDataSync.Drivers.DuckDb.Tests;

public sealed class DuckDbDriverTests
{
    private static ConnectionConfig Config(string? connectionString = null) => new()
    {
        Name = "duck",
        DriverType = DriverIds.DuckDb,
        AuthMode = AuthMode.None,
        ConnectionString = connectionString,
    };

    /// <summary>
    /// Empty, and meant — a query-first source reads what its scanners reach, not the catalog of the
    /// database the query runs in. Asserted so that "returns nothing" stays a stated answer rather
    /// than decaying into "throws once somebody browses it".
    /// </summary>
    [Fact]
    public async Task Introspection_ReturnsEmptyListsWithoutThrowing()
    {
        var driver = new DuckDbDriver();
        await using var connection = driver.CreateConnection(Config(), null);
        await connection.OpenAsync();

        Assert.Empty(await driver.ListDatabasesAsync(connection, CancellationToken.None));
        Assert.Empty(await driver.ListTablesAsync(connection, "main", CancellationToken.None));
        Assert.Empty(await driver.ListColumnsAsync(connection, "main", "main", "t", CancellationToken.None));
    }

    /// <summary>Even with real tables in front of it. The lists are empty because the driver says so,
    /// not because the database happened to be.</summary>
    [Fact]
    public async Task Introspection_StaysEmptyEvenWhenTheDatabaseHasTables()
    {
        var driver = new DuckDbDriver();
        await using var connection = driver.CreateConnection(Config(), null);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (Id INTEGER)";
        await cmd.ExecuteNonQueryAsync();

        Assert.Empty(await driver.ListTablesAsync(connection, "main", CancellationToken.None));
    }

    /// <summary>
    /// A bare path is what every DuckDB example takes and therefore what an operator types;
    /// <c>DuckDBConnection</c> takes ADO.NET syntax and throws on one. The driver reads the obvious
    /// meaning rather than bouncing back a syntax error about a syntax nobody mentioned.
    /// <para>
    /// The expected values are lower-cased because every form goes out through
    /// <c>DuckDBConnectionStringBuilder</c>, which canonicalizes the key — including the one built
    /// from the <c>InMemory</c> default, so there is one output shape rather than two that differ by
    /// which branch produced them.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, "data source=:memory:")]
    [InlineData("", "data source=:memory:")]
    [InlineData(":memory:", "data source=:memory:")]
    [InlineData("/var/lib/warehouse.duckdb", "data source=/var/lib/warehouse.duckdb")]
    [InlineData("Data Source=/var/lib/warehouse.duckdb", "data source=/var/lib/warehouse.duckdb")]
    public void ConnectionString_NormalizesABarePathAndDefaultsToInMemory(string? configured, string expected)
    {
        Assert.Equal(expected, DuckDbDriver.BuildConnectionString(Config(configured)));
    }

    [Fact]
    public void ConnectionString_CarriesTheFreeFormPropertiesBag()
    {
        var config = Config(":memory:");
        config.Properties["access_mode"] = "READ_ONLY";

        Assert.Contains("access_mode=READ_ONLY", DuckDbDriver.BuildConnectionString(config));
    }

    [Fact]
    public async Task TestAsync_ReportsTheEngineVersion()
    {
        var driver = new DuckDbDriver();
        await using var connection = driver.CreateConnection(Config(), null);
        await connection.OpenAsync();

        var result = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("DuckDB v", result.ServerVersion);
    }

    /// <summary>
    /// Source-only, and this is the assertion that keeps it honest: a writer added later has to be a
    /// decision somebody made rather than something that arrived with a copied constructor.
    /// </summary>
    [Fact]
    public void OffersOneReaderAndNoWriteSide()
    {
        var driver = new DuckDbDriver();

        Assert.Equal(DuckDbQueryReader.ReaderKind, Assert.Single(driver.Readers).Kind);
        Assert.Empty(driver.StagingProviders);
        Assert.Empty(driver.Writers);
    }

    /// <summary>
    /// Auto-segment discovery is not implemented, and the capability endpoint derives that from the
    /// interface rather than from a list — so this is what the SPA's reader picker will say about it.
    /// </summary>
    [Fact]
    public void TheQueryReader_DoesNotExpandSegments()
    {
        var registry = new DriverRegistry();
        registry.Register(new DuckDbDriver());

        var capability = Assert.Single(registry.Describe(DriverIds.DuckDb)!.Readers);

        Assert.False(capability.SupportsSegmentation);
        Assert.False(capability.DetectsDeletes);
    }

    /// <summary>The one setting, declared as SQL — which is what puts an editor on the mapping's
    /// source tab instead of a one-line box.</summary>
    [Fact]
    public void TheQueryOption_IsDeclaredAsSql()
    {
        var parameter = Assert.Single(new DuckDbQueryReader().Parameters);

        Assert.Equal(DuckDbQueryReader.QueryOption, parameter.Name);
        Assert.Equal(ParameterType.Sql, parameter.Type);
        Assert.True(parameter.Required);
    }
}
