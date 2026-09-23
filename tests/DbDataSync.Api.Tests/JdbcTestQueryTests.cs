using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Reproduces a reported bug: a JDBC-backed driver.yaml's own testQuery (and a connection's explicit
/// override of it) appeared not to run when "Test connection" was clicked. Every layer below the real
/// API host already checked out clean in isolation (YAML parsing, JdbcDriverSpec.TestQuery, a real
/// command over a real JdbcConnection) — this is the one thing none of those cover: the real
/// DriverLoader-registered driver, reached through POST /api/connections/{name}/test exactly as the
/// SPA calls it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcTestQueryTests : IClassFixture<JdbcTestQueryApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    private static string JdbcUrl =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_JDBC_URL")
        ?? "jdbc:postgresql://localhost:15432/";

    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly string _databaseName = $"dbdatasync_jdbc_testquery_api_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"jdbc-testquery-conn-{Guid.NewGuid():N}";

    public JdbcTestQueryTests(JdbcTestQueryApiFactory factory)
    {
        _client = factory.CreateClient(); // first touch — builds the host, loading the driver.yaml.
        _secrets = factory.Services.GetRequiredService<SecretStore>();
    }

    public async Task InitializeAsync()
    {
        await using var bootstrap = new NpgsqlConnection(ServerConnectionString);
        await bootstrap.OpenAsync();
        await using var create = bootstrap.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{_databaseName}\";";
        await create.ExecuteNonQueryAsync();

        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), "DbDataSync_Test_Pw1");

        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = JdbcTestQueryApiFactory.DriverId,
            ConnectionString = $"{JdbcUrl}{_databaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), null);

        await using var connection = new NpgsqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
        await drop.ExecuteNonQueryAsync();
    }

    private sealed record QueryPreviewResultDto(
        string Source, List<string> Columns, List<List<string?>> Rows, bool Truncated, string? Error);
    private sealed record ReportDto(bool Succeeded, string? Error, QueryPreviewResultDto? TestQueryResult);

    /// <summary>
    /// No explicit ConnectionConfig.TestQuery — this connection must fall back to the driver.yaml's own
    /// testQuery, resolved through the real, DriverLoader-registered JdbcGenericDriver instance rather
    /// than one this test constructed itself.
    /// </summary>
    [Fact]
    public async Task Test_WithNoOverride_FallsBackToTheDriverYamlsOwnTestQuery()
    {
        var capabilities = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/connections/{_connectionName}/capabilities");
        Assert.Equal(JdbcTestQueryApiFactory.TestQueryText, capabilities.GetProperty("defaultTestQuery").GetString());

        var response = await _client.PostAsync($"/api/connections/{_connectionName}/test", null);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<ReportDto>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.NotNull(report.TestQueryResult);
        Assert.Null(report.TestQueryResult!.Error);
        Assert.Equal(["one", "db"], report.TestQueryResult.Columns);
        Assert.Equal("1", Assert.Single(report.TestQueryResult.Rows)[0]);
    }

    /// <summary>The connection's own override wins over the driver's default — same contract the
    /// built-in-driver version of this test (ConnectionTestIntegrationTests) already proves, checked
    /// again here because that test never touches a descriptor-loaded driver at all.</summary>
    [Fact]
    public async Task Test_WithAnExplicitOverride_RunsThatInsteadOfTheDriversDefault()
    {
        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = JdbcTestQueryApiFactory.DriverId,
            ConnectionString = $"{JdbcUrl}{_databaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
            Password = "DbDataSync_Test_Pw1",
            TestQuery = "SELECT 42 AS answer",
        }, JsonOptions)).EnsureSuccessStatusCode();

        var response = await _client.PostAsync($"/api/connections/{_connectionName}/test", null);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<ReportDto>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.NotNull(report.TestQueryResult);
        Assert.Equal(["answer"], report.TestQueryResult!.Columns);
        Assert.Equal("42", Assert.Single(report.TestQueryResult.Rows)[0]);
    }

    [Fact]
    public async Task Validate_EchoesTheTestQueryBackVerbatim()
    {
        var response = await _client.PostAsJsonAsync("/api/drivers/validate", new
        {
            yaml = $"""
                id: {JdbcTestQueryApiFactory.DriverId}-validate
                displayName: Postgres (via JDBC, validate echo)
                library: ikvm
                base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
                jdbc:
                  driverClass: org.postgresql.Driver
                  driverJarPaths: [postgresql.jar]
                testQuery: {JdbcTestQueryApiFactory.TestQueryText}
                dialect:
                  quoteIdentifier: doubleQuote
                  parameterPrefix: "@"
                  rowLimit: limitOffset
                typeMap:
                  int4: Int32
                capabilities:
                  readers: []
                  staging: []
                  writers: []
                """,
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Equal(
            JdbcTestQueryApiFactory.TestQueryText,
            body.GetProperty("interpreted").GetProperty("testQuery").GetString());
    }
}
