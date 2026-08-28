using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using DataSync.Core.Secrets;
using Microsoft.Data.SqlClient;

namespace DataSync.Api.Tests;

/// <summary>
/// The preview, against real databases — and the one test that keeps it honest.
/// <para>
/// A preview that could drift from what a pass runs is worse than no preview, because it looks
/// authoritative. So this does not compare the preview's SQL to a string: it **executes** the
/// statement the preview showed and asserts the pass loads exactly what that statement returns. If
/// the two ever diverge, this fails, whatever the text says.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreviewIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SOURCE_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client;
    private readonly string _databaseName = $"DataSyncPreview_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"preview-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"preview-repl-{Guid.NewGuid():N}";

    public PreviewIntegrationTests(TestApiFactory factory) => _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        await using var connection = OpenDatabase();
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(connection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(connection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'alice'), (2, 'bob'), (3, 'carol');");
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(connection, "CREATE TABLE dbo.PreviewHookLog (Note NVARCHAR(100) NOT NULL);");

        Environment.SetEnvironmentVariable(
            SecretRefs.EnvironmentVariableFor(SecretRefs.ForConnection(_connectionName)), "DataSync_Test_Pw1");

        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = true,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
                Target = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        // A literal transform and a hook, so the preview has an operator-authored statement of each
        // kind to attribute — the whole point being that the origin of every line is visible.
        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name", Transform = "UPPER({{column}})" },
                ],
                Hooks = new Dictionary<string, List<HookConfig>?>
                {
                    ["beforeLoad"] = [new HookConfig { Name = "note-the-load", Sql = "INSERT INTO dbo.PreviewHookLog (Note) VALUES ('before load');" }],
                },
            }, JsonOptions)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            SecretRefs.EnvironmentVariableFor(SecretRefs.ForConnection(_connectionName)), null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    [Fact]
    public async Task ThePreviewsSourceRead_ReturnsExactlyWhatThePassLoads()
    {
        var preview = await GetPreviewAsync();

        var read = Assert.Single(preview.Statements.Where(s => s.Stage == "Source read" && s.Sql is not null));
        Assert.Contains("UPPER(", read.Sql!);

        // Executed, not compared. Whatever the preview says it will read, that is what must arrive at
        // the target — and a statement that has drifted from the reader's own fails here.
        var previewRows = await QueryAsync(read.Sql!);

        await TriggerAndWaitAsync();

        var targetRows = await QueryAsync($"SELECT Id, Name FROM dbo.[{_targetTable}];");
        Assert.Equal(new Dictionary<int, string> { [1] = "ALICE", [2] = "BOB", [3] = "CAROL" }, targetRows);
        Assert.Equal(previewRows, targetRows);
    }

    [Fact]
    public async Task ThePreviewDescribesEveryStageInTheOrderAPassRunsThem_WithEachOnesOrigin()
    {
        var preview = await GetPreviewAsync();

        // The order is the point: read as a description of what happens, out-of-order would be wrong.
        var stages = preview.Statements.Select(s => s.Stage).Distinct().ToList();
        Assert.Equal(["Source read", "Staging", "Before load", "Write"], stages);
        Assert.Empty(preview.Problems);

        var hook = Assert.Single(preview.Statements.Where(s => s.Stage == "Before load"));
        Assert.Equal("Hook: note-the-load", hook.Title);
        Assert.Equal("OperatorSql", hook.Origin);
        Assert.Contains("PreviewHookLog", hook.Sql!);

        // Staging is DDL plus a bulk load that has no statement — named rather than invented.
        var staging = preview.Statements.Where(s => s.Stage == "Staging").ToList();
        Assert.Contains(staging, s => s.Sql?.StartsWith("CREATE TABLE #Staging_") == true);
        Assert.Contains(staging, s => s.Sql is null && s.Detail!.Contains("SqlBulkCopy"));

        var write = Assert.Single(preview.Statements.Where(s => s.Stage == "Write"));
        Assert.Contains("MERGE INTO", write.Sql!);
    }

    /// <summary>
    /// The reader's statement depends on the stored watermark, so the preview has to as well. A
    /// preview that always showed the first-pass form would be right exactly once.
    /// </summary>
    [Fact]
    public async Task AfterAPass_ThePreviewShowsTheIncrementalReadRatherThanTheFullLoad()
    {
        var before = await GetPreviewAsync();
        Assert.Contains(
            before.Statements,
            s => s.Stage == "Source read" && s.Title.StartsWith("Full load"));

        await TriggerAndWaitAsync();

        var after = await GetPreviewAsync();
        var read = Assert.Single(after.Statements.Where(s => s.Stage == "Source read" && s.Sql is not null));
        Assert.StartsWith("Incremental read of changes after version", read.Title);
        Assert.Contains("CHANGETABLE", read.Sql!);
    }

    private sealed record PreviewStatementDto(string Stage, string Title, string? Sql, string Origin, string? Detail);
    private sealed record PreviewReportDto(List<PreviewStatementDto> Statements, List<string> Problems);

    private async Task<PreviewReportDto> GetPreviewAsync()
    {
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/preview");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewReportDto>(JsonOptions))!;
    }

    private async Task<Dictionary<int, string>> QueryAsync(string sql)
    {
        await using var connection = OpenDatabase();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new Dictionary<int, string>();
        while (await reader.ReadAsync())
            rows[reader.GetInt32(0)] = reader.GetString(1);
        return rows;
    }

    private async Task TriggerAndWaitAsync()
    {
        var trigger = await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        trigger.EnsureSuccessStatusCode();
        var body = await trigger.Content.ReadFromJsonAsync<JsonElement>();
        var runId = Guid.Parse(body.GetProperty("runIds").EnumerateArray().Single().GetString()!);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/runs/{runId}");
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                var status = run.GetProperty("status").GetString();
                if (status == "Succeeded")
                    return;
                if (status is "Failed" or "Cancelled")
                    throw new InvalidOperationException($"Run {runId} {status}: {run.GetProperty("errorSummary")}");
            }
            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not finish within 30s.");
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
