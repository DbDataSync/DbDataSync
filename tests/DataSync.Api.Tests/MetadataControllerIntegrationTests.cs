using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Api.Tests;

[Trait("Category", "Integration")]
public sealed class MetadataControllerIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _databaseName = $"DataSyncApiMetaTest_{Guid.NewGuid():N}";

    public MetadataControllerIntegrationTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE DATABASE [{_databaseName}];");

        var dbBuilder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        await using var dbConnection = new SqlConnection(dbBuilder.ConnectionString);
        await dbConnection.OpenAsync();
        await ExecuteAsync(dbConnection, "CREATE TABLE dbo.Probe (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> CreateConnectionAsync()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
        }, JsonOptions);
        return name;
    }

    [Fact]
    public async Task ListDatabases_ReturnsCreatedDatabase()
    {
        var connectionName = await CreateConnectionAsync();

        var databases = await _client.GetFromJsonAsync<List<string>>(
            $"/api/connections/{connectionName}/metadata/databases", JsonOptions);

        Assert.Contains(_databaseName, databases!);
    }

    [Fact]
    public async Task ListTables_ReturnsCreatedTable()
    {
        var connectionName = await CreateConnectionAsync();

        var tables = await _client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/connections/{connectionName}/metadata/databases/{_databaseName}/tables", JsonOptions);

        Assert.Contains(tables!, t => t.GetProperty("table").GetString() == "Probe");
    }

    [Fact]
    public async Task ListColumns_ReturnsExpectedColumns()
    {
        var connectionName = await CreateConnectionAsync();

        var columns = await _client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/connections/{connectionName}/metadata/databases/{_databaseName}/schemas/dbo/tables/Probe/columns",
            JsonOptions);

        Assert.Contains(columns!, c => c.GetProperty("name").GetString() == "Id" && c.GetProperty("isPrimaryKey").GetBoolean());
        Assert.Contains(columns!, c => c.GetProperty("name").GetString() == "Name");
    }
}
