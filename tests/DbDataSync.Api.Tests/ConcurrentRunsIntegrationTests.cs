using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Answers detailed-design.md §8's open "central SQLite contention in practice" question with real
/// data (architecture/implementation/done/phase-007-e2e-validation.md): triggers several *independent*
/// replications at once, so several real DbDataSync.TaskRunner child processes are genuinely writing to
/// the shared central state database concurrently — the actual scenario the WAL/busy_timeout/retry
/// mitigations in StateDatabase/SqliteRetry exist for, not just one task's sequential runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConcurrentRunsIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private const int ConcurrentReplicationCount = 8;
    private const int RowsPerTable = 25;

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
    private readonly string _databaseName = $"DbDataSyncConcurrencyTest_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"conc-src-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"conc-tgt-{Guid.NewGuid():N}";

    public ConcurrentRunsIntegrationTests(TestApiFactory factory)
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

        for (var i = 0; i < ConcurrentReplicationCount; i++)
        {
            await ExecuteAsync(dbConnection, $"CREATE TABLE dbo.[Src_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
            await ExecuteAsync(dbConnection, $"ALTER TABLE dbo.[Src_{i}] ENABLE CHANGE_TRACKING;");
            await ExecuteAsync(dbConnection, $"CREATE TABLE dbo.[Tgt_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

            var values = string.Join(", ", Enumerable.Range(0, RowsPerTable).Select(row => $"({row}, 'Row{i}_{row}')"));
            await ExecuteAsync(dbConnection, $"INSERT INTO dbo.[Src_{i}] (Id, Name) VALUES {values};");
        }

        SetSecretEnvVar(_srcConnectionName, "DbDataSync_Test_Pw1");
        SetSecretEnvVar(_tgtConnectionName, "DbDataSync_Test_Pw1");

        await CreateConnectionAsync(_srcConnectionName);
        await CreateConnectionAsync(_tgtConnectionName);

        await Task.WhenAll(Enumerable.Range(0, ConcurrentReplicationCount).Select(CreateReplicationAsync));
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

    [Fact]
    public async Task TriggeringManyReplicationsAtOnce_AllSucceed_WithNoStateStoreContentionFailures()
    {
        var triggerResponses = await Task.WhenAll(Enumerable.Range(0, ConcurrentReplicationCount).Select(TriggerAsync));

        var runIds = new List<Guid>();
        foreach (var response in triggerResponses)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Each of these replications has exactly one table mapping, so "runIds" always has a
            // single element — a trigger now enqueues one Primary pass per mapping.
            runIds.Add(Guid.Parse(body.GetProperty("runIds").EnumerateArray().Single().GetString()!));
        }

        var finalRuns = await Task.WhenAll(runIds.Select(PollUntilTerminalAsync));

        var failures = finalRuns
            .Where(r => r.GetProperty("status").GetString() != "Succeeded")
            .Select(r => $"{r.GetProperty("runId").GetString()}: {r.GetProperty("status").GetString()} — {r.GetProperty("errorSummary").GetString()}")
            .ToList();
        Assert.True(failures.Count == 0, "Expected every concurrently-triggered run to succeed, but got:\n" + string.Join("\n", failures));

        // Phase 134: each replication's reader (Change Tracking) captures a position ahead of its
        // first pass rather than reading directly, so every Primary run's own rowsRead/rowsWritten
        // above is 0 by design — the real load happens on the Bulk Load each pass requested. Wait for
        // each to finish, then check its row instead.
        var loadRuns = await Task.WhenAll(Enumerable.Range(0, ConcurrentReplicationCount).Select(async i =>
        {
            var replicationName = $"repl-{i}-{_databaseName}";
            await _client.WaitForLoadToCompleteAsync(replicationName, "main");
            var history = await _client.GetFromJsonAsync<JsonElement>(
                $"/api/replications/{replicationName}/runs?kind=BulkLoad&mappingName=main&limit=1", JsonOptions);
            return history.GetProperty("runs").EnumerateArray().Single();
        }));

        foreach (var run in loadRuns)
        {
            Assert.Equal(RowsPerTable, run.GetProperty("rowsRead").GetInt64());
            Assert.Equal(RowsPerTable, run.GetProperty("rowsWritten").GetInt64());
        }

        // The real proof, independent of what the API/state store claims: every target table actually
        // has the right row count.
        var dbBuilder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        await using var dbConnection = new SqlConnection(dbBuilder.ConnectionString);
        await dbConnection.OpenAsync();
        for (var i = 0; i < ConcurrentReplicationCount; i++)
        {
            await using var cmd = dbConnection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[Tgt_{i}];";
            var count = (int)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(RowsPerTable, count);
        }
    }

    private async Task<JsonElement> PollUntilTerminalAsync(Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // 404 is expected and transient here: the spawned TaskRunner process hasn't necessarily
            // written its first TaskRuns row yet by the time this first polls.
            var response = await _client.GetAsync($"/api/runs/{runId}");
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                var status = run.GetProperty("status").GetString();
                if (status is "Succeeded" or "Failed" or "Cancelled")
                    return run;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not reach a terminal status within 60s.");
    }

    private Task<HttpResponseMessage> TriggerAsync(int index) =>
        _client.PostAsync($"/api/replications/repl-{index}-{_databaseName}/runs", new StringContent("", Encoding.UTF8, "application/json"));

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

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new Exception($"{(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    private async Task CreateReplicationAsync(int index)
    {
        var name = $"repl-{index}-{_databaseName}";
        await EnsureSuccessWithBodyAsync(await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Enabled = true,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions));

        await EnsureSuccessWithBodyAsync(await _client.PutAsJsonAsync($"/api/replications/{name}/table-mappings/main", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = _srcConnectionName, Database = _databaseName, Schema = "dbo", Table = $"Src_{index}" }],
            Targets = [new TableSpec { ConnectionName = _tgtConnectionName, Database = _databaseName, Schema = "dbo", Table = $"Tgt_{index}" }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, JsonOptions));

        // The PUT saves the mapping without introspecting — phase 90 captures the cache from the
        // columns a client sends, and a hand-built TableMappingConfig sends none, leaving phase 91's
        // readers and writers nothing to run from. This is the Refresh metadata endpoint, which reads
        // both catalogs server-side.
        await EnsureSuccessWithBodyAsync(await _client.PostAsync(
            $"/api/replications/{name}/table-mappings/main/refresh-metadata", null));
    }
}
