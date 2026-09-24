using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Proves the raw-query source isn't a DuckDB-only feature: a real MsSql connection — an engine with a
/// real catalog, unlike DuckDB — offers the same "a mapping's source is a query I wrote, not a table"
/// capability, under the neutral "Query" Kind, and a real pass through it lands real rows. This is the
/// end-to-end counterpart to <see cref="DuckDbQueryPreviewTests"/>, which only ever proves the feature
/// against the one engine it began on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RawQuerySourceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>, IAsyncLifetime
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
    private readonly SecretStore _secrets = factory.Services.GetRequiredService<SecretStore>();
    private readonly string _databaseName = $"DbDataSyncRawQuery_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"rawquery-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"rawquery-repl-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
        }

        await using var connection = OpenDatabase();
        // A NOT NULL int key and a nullable nvarchar column — enough to prove real types/nullability
        // come back, not just names.
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NULL);");
        await ExecuteAsync(connection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Region) VALUES (1, 'north'), (2, NULL);");
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NULL);");

        // The spawned TaskRunner child process a real trigger uses has its own SecretStore reading the
        // real OS environment, not this test host's in-memory one — RunLifecycleIntegrationTests' own
        // pattern.
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), "DbDataSync_Test_Pw1");

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
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    /// <summary>The registration mechanism's own claim: an MsSql connection — which already has its own
    /// catalog-backed readers — additionally offers the neutral "Query" reader, not "DuckDbQuery".</summary>
    [Fact]
    public async Task AnMsSqlConnection_OffersTheNeutralQueryReader_NotDuckDbsOwnName()
    {
        var response = await _client.GetAsync($"/api/connections/{_connectionName}/capabilities");
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var readers = capabilities.GetProperty("readers").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).ToList();
        Assert.Contains("Query", readers);
        Assert.DoesNotContain("DuckDbQuery", readers);

        var queryReader = capabilities.GetProperty("readers").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "Query");
        var parameter = Assert.Single(queryReader.GetProperty("parameters").EnumerateArray());
        Assert.Equal("query", parameter.GetProperty("name").GetString());
        Assert.Equal("Sql", parameter.GetProperty("type").GetString());
    }

    /// <summary>
    /// The claim this whole feature exists to prove: a query's own result-set shape, read off a real
    /// SqlClient DataReader — an int key that comes back non-nullable, a nullable nvarchar that comes
    /// back nullable, both with their own real native type name. Not the name-only, blank-typed metadata
    /// a query source got before this.
    /// </summary>
    [Fact]
    public async Task PreviewingAQueryAgainstMsSql_ReturnsRealColumnTypesAndNullability_NotBlanks()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{_connectionName}/query-preview",
            new { query = $"SELECT Id, Region FROM dbo.[{_sourceTable}]", sampleRows = 20 },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<QueryPreviewResult>(JsonOptions))!;

        Assert.Null(result.Error);
        Assert.Equal(["Id", "Region"], result.Columns);
        Assert.NotNull(result.ColumnMetadata);

        var id = result.ColumnMetadata!.Single(c => c.Name == "Id");
        Assert.False(id.IsNullable);
        Assert.Contains("int", id.NativeType, StringComparison.OrdinalIgnoreCase);

        var region = result.ColumnMetadata.Single(c => c.Name == "Region");
        Assert.True(region.IsNullable);
        Assert.Contains("char", region.NativeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End to end: a mapping whose source is a raw query against MsSql — not DuckDB, not a table —
    /// triggered as a real pass, landing real rows in a real target table. The reader's own contract
    /// (every row an insert, no incremental behaviour) proven by running it, not just by reading its
    /// source.
    /// </summary>
    [Fact]
    public async Task ARealPassThroughAQuerySource_LandsTheQuerysOwnRows()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig
                {
                    Kind = "Query",
                    Options = { ["query"] = $"SELECT Id, Region FROM dbo.[{_sourceTable}] WHERE Id = 1" },
                },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlDeleteInsert" },
            },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
                Target = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
        {
            Name = "main",
            // Schema/Table left blank — the established "this is a query source" convention every
            // other query-source mapping already uses.
            Sources = [new SourceTableSpec { Schema = "", Table = "" }],
            Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Region", TargetColumn = "Region" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // The PUT above doesn't introspect — this is the server-side column-metadata capture the
        // writer needs cached for the target side (the source side stays empty: a query source has no
        // catalog to read, per MappingColumnReader's own "no table named" branch).
        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/refresh-metadata", null))
            .EnsureSuccessStatusCode();

        var trigger = await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        trigger.EnsureSuccessStatusCode();
        var body = await trigger.Content.ReadFromJsonAsync<JsonElement>();
        var runId = Guid.Parse(body.GetProperty("runIds").EnumerateArray().Single().GetString()!);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string? status = null;
        string? errorSummary = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var runResponse = await _client.GetAsync($"/api/runs/{runId}");
            if (runResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var run = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
                status = run.GetProperty("status").GetString();
                if (run.TryGetProperty("errorSummary", out var err))
                    errorSummary = err.GetString();
                if (status is "Succeeded" or "Failed" or "Cancelled")
                    break;
            }
            await Task.Delay(250);
        }
        if (status != "Succeeded")
            throw new Exception($"Run status was '{status}': {errorSummary}");

        await using var targetConnection = OpenDatabase();
        await using var cmd = targetConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Region FROM dbo.[{_targetTable}];";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("north", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
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
