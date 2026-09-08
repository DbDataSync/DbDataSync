using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The full Phase 5 exit criteria in one test: creating a replication via the API and hitting the
/// manual-trigger endpoint spawns a real DbDataSync.TaskRunner process, status transitions are visible
/// over the SignalR hub, and the final TaskRuns row is correct — all without restarting the API.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunLifecycleIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly string _databaseName = $"DbDataSyncApiRunTest_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"src-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"tgt-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"repl-{Guid.NewGuid():N}";

    public RunLifecycleIntegrationTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        var dbBuilder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        await using var dbConnection = new SqlConnection(dbBuilder.ConnectionString);
        await dbConnection.OpenAsync();
        await ExecuteAsync(dbConnection, $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(dbConnection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(dbConnection, $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(dbConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        // The spawned DbDataSync.TaskRunner is a real child OS process with its own SecretStore — it
        // doesn't share the test host's in-memory secret cache (see TestApiFactory). Process.Start
        // inherits the current process's environment by default, so setting the env-var fallback
        // here reaches the child too. Mirrors how Phase 4's manual CLI run resolved this in the same
        // sandbox (no OS keychain available here).
        SetSecretEnvVar(_srcConnectionName, "DbDataSync_Test_Pw1");
        SetSecretEnvVar(_tgtConnectionName, "DbDataSync_Test_Pw1");

        await CreateConnectionAsync(_srcConnectionName);
        await CreateConnectionAsync(_tgtConnectionName);
        await CreateReplicationAsync();
    }

    public async Task DisposeAsync()
    {
        ClearSecretEnvVar(_srcConnectionName);
        ClearSecretEnvVar(_tgtConnectionName);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    private void SetSecretEnvVar(string connectionName, string password) =>
        Environment.SetEnvironmentVariable(SecretEnvVarName(connectionName), password);

    private void ClearSecretEnvVar(string connectionName) =>
        Environment.SetEnvironmentVariable(SecretEnvVarName(connectionName), null);

    private string SecretEnvVarName(string connectionName) =>
        _secrets.EnvName(SecretRefs.ForConnection(connectionName));

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateConnectionAsync(string name) =>
        (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateReplicationAsync()
    {
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
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = _srcConnectionName, Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = _tgtConnectionName, Database = _databaseName, Schema = "dbo", Table = _targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // The PUT saves the mapping; it does not introspect. Phase 90 captures a mapping's column
        // metadata from the columns the *client* sends — the editor sends the ones it already fetched
        // for its pickers, and a test PUTting a hand-built TableMappingConfig sends none. Phase 91's
        // readers and writers then refuse to run against the empty cache that leaves. This is the
        // endpoint behind the editor's Refresh metadata button, and the server-side introspection that
        // actually fills it.
        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Trigger_SpawnsRealProcess_BroadcastsOverSignalR_AndRecordsCorrectFinalStatus()
    {
        await using var hubConnection = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/run", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                // TestServer's in-memory pipeline doesn't support real WebSockets — LongPolling is
                // the standard transport for SignalR-over-WebApplicationFactory testing.
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var logLines = new List<string>();
        JsonElement? completedPayload = null;
        var completedTcs = new TaskCompletionSource();

        hubConnection.On<JsonElement>("logLine", entry => logLines.Add(entry.GetProperty("message").GetString() ?? ""));
        hubConnection.On<JsonElement>("runCompleted", payload =>
        {
            completedPayload = payload;
            completedTcs.TrySetResult();
        });

        await hubConnection.StartAsync();

        var triggerResponse = await _client.PostAsync($"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        triggerResponse.EnsureSuccessStatusCode();
        var triggerBody = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        // A trigger now enqueues one Primary pass per table mapping and returns all of their RunIds —
        // this replication has exactly one mapping, so its single element is the run to watch.
        var runId = triggerBody.GetProperty("runIds").EnumerateArray().Single().GetString();

        await hubConnection.InvokeAsync("JoinRun", runId);

        var completedTask = await Task.WhenAny(completedTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(completedTcs.Task, completedTask);

        Assert.NotNull(completedPayload);
        Assert.Equal("Succeeded", completedPayload!.Value.GetProperty("status").GetString());
        Assert.Equal(2, completedPayload.Value.GetProperty("rowsRead").GetInt64());
        Assert.Equal(2, completedPayload.Value.GetProperty("rowsWritten").GetInt64());
        Assert.NotEmpty(logLines);

        var runDetail = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        Assert.Equal("Succeeded", runDetail.GetProperty("status").GetString());
        Assert.True(runDetail.GetProperty("pid").GetInt32() > 0);
    }
}
