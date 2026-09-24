using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Microsoft.Data.SqlClient;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 186J: refreshing a mapping's metadata captures each declared relationship's own foreign
/// table shape, real types included, into <see cref="TableMappingConfig.RelationshipColumns"/> — the
/// same catalog-read path <see cref="MappingColumnReader"/> already uses for the primary source and
/// target, applied to a relationship's foreign table too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RelationshipMetadataRefreshTests(TestApiFactory factory) : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SOURCE_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _databaseName = $"DbDataSyncRelMeta_{Guid.NewGuid():N}";
    private readonly string _ordersTable = $"Orders_{Guid.NewGuid():N}";
    private readonly string _customersTable = $"Customers_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"relmeta-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"relmeta-repl-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
        }

        await using var connection = OpenDatabase();
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_customersTable}] (Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NULL);");
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_ordersTable}] (Id INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);");

        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlDeleteInsert" },
            },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
                Target = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    [Fact]
    public async Task RefreshMetadata_CapturesTheRelationshipsForeignTable_WithRealTypes()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = _ordersTable }],
            Targets = [new TableSpec { Schema = "dbo", Table = _ordersTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Region", TargetColumn = "CustomerRegion", Relationship = "Customer" },
            ],
            Relationships =
            [
                new RelationshipConfig
                {
                    Name = "Customer",
                    Schema = "dbo",
                    Table = _customersTable,
                    JoinKeys = [new RelationshipJoinKey { LocalColumn = "CustomerId", ForeignColumn = "Id" }],
                },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        var refresh = await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/refresh-metadata", null);
        refresh.EnsureSuccessStatusCode();
        var result = (await refresh.Content.ReadFromJsonAsync<JsonElement>(JsonOptions));

        var mapping = result.GetProperty("mapping");
        var relationshipColumns = mapping.GetProperty("relationshipColumns");
        var customerColumns = relationshipColumns.GetProperty("Customer").EnumerateArray().ToList();

        var id = customerColumns.Single(c => c.GetProperty("name").GetString() == "Id");
        Assert.False(id.GetProperty("isNullable").GetBoolean());
        Assert.True(id.GetProperty("isPrimaryKey").GetBoolean());
        Assert.Contains("int", id.GetProperty("nativeType").GetString(), StringComparison.OrdinalIgnoreCase);

        var region = customerColumns.Single(c => c.GetProperty("name").GetString() == "Region");
        Assert.True(region.GetProperty("isNullable").GetBoolean());
        Assert.Contains("char", region.GetProperty("nativeType").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshMetadata_WithNoRelationshipsDeclared_LeavesRelationshipColumnsEmpty()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/plain", new TableMappingConfig
        {
            Name = "plain",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = _ordersTable }],
            Targets = [new TableSpec { Schema = "dbo", Table = _ordersTable }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
        }, JsonOptions)).EnsureSuccessStatusCode();

        var refresh = await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/plain/refresh-metadata", null);
        refresh.EnsureSuccessStatusCode();
        var result = await refresh.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var relationshipColumns = result.GetProperty("mapping").GetProperty("relationshipColumns");
        Assert.Empty(relationshipColumns.EnumerateObject());
    }

    private SqlConnection OpenDatabase()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
