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
/// Phase 7's end-to-end validation (architecture/implementation-plan.md § Phase 7,
/// architecture/implementation/done/phase-007-e2e-validation.md): source and target connections point at
/// two genuinely separate SQL Server *instances* (docker-compose.yml's mssql-source/mssql-target,
/// distinct containers on distinct ports) rather than two databases sharing one server, and the whole
/// scenario — initial full load, then two full insert/update/delete cycles — is confirmed by querying
/// the real target database directly rather than trusting the API's own reported row counts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossInstanceEndToEndTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SourceServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SOURCE_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private static string TargetServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_TARGET_SERVER")
        ?? "Data Source=localhost,14331;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly string _databaseName = $"DbDataSyncE2E_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"e2e-src-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"e2e-tgt-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"e2e-repl-{Guid.NewGuid():N}";

    public CrossInstanceEndToEndTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(SourceServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        await using (var bootstrap = new SqlConnection(TargetServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
        }

        await using (var srcConnection = OpenDatabaseConnection(SourceServerConnectionString))
        {
            await ExecuteAsync(srcConnection, $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
            await ExecuteAsync(srcConnection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
            await ExecuteAsync(srcConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol');");
        }

        await using (var tgtConnection = OpenDatabaseConnection(TargetServerConnectionString))
            await ExecuteAsync(tgtConnection, $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

        SetSecretEnvVar(_srcConnectionName, "DbDataSync_Test_Pw1");
        SetSecretEnvVar(_tgtConnectionName, "DbDataSync_Test_Pw1");

        await CreateConnectionAsync(_srcConnectionName, host: "localhost", port: 14330);
        await CreateConnectionAsync(_tgtConnectionName, host: "localhost", port: 14331);
        await CreateReplicationAsync();
    }

    public async Task DisposeAsync()
    {
        ClearSecretEnvVar(_srcConnectionName);
        ClearSecretEnvVar(_tgtConnectionName);

        await using (var connection = new SqlConnection(SourceServerConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
        }

        await using (var connection = new SqlConnection(TargetServerConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
        }
    }

    [Fact]
    public async Task InitialLoad_ThenTwoInsertUpdateDeleteCycles_ReplicateCorrectlyAcrossRealInstances()
    {
        // --- Run 1: initial full load ---
        var run1 = await TriggerAndWaitAsync();
        Assert.Equal("Succeeded", run1.GetProperty("status").GetString());
        Assert.Equal(3, run1.GetProperty("rowsRead").GetInt64());
        Assert.Equal(3, run1.GetProperty("rowsWritten").GetInt64());
        await AssertTargetRowsAsync(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob", [3] = "Carol" });

        // --- Cycle 1: insert Dave (4), update Bob -> Bobby (2), delete Alice (1) ---
        await using (var srcConnection = OpenDatabaseConnection(SourceServerConnectionString))
        {
            await ExecuteAsync(srcConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (4, 'Dave');");
            await ExecuteAsync(srcConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Bobby' WHERE Id = 2;");
            await ExecuteAsync(srcConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 1;");
        }

        var run2 = await TriggerAndWaitAsync();
        Assert.Equal("Succeeded", run2.GetProperty("status").GetString());
        Assert.Equal(3, run2.GetProperty("rowsRead").GetInt64());
        Assert.Equal(3, run2.GetProperty("rowsWritten").GetInt64());
        await AssertTargetRowsAsync(new Dictionary<int, string> { [2] = "Bobby", [3] = "Carol", [4] = "Dave" });

        // --- Cycle 2: insert Eve (5), update Carol -> Caroline (3), delete Dave (4) ---
        await using (var srcConnection = OpenDatabaseConnection(SourceServerConnectionString))
        {
            await ExecuteAsync(srcConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (5, 'Eve');");
            await ExecuteAsync(srcConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Caroline' WHERE Id = 3;");
            await ExecuteAsync(srcConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 4;");
        }

        var run3 = await TriggerAndWaitAsync();
        Assert.Equal("Succeeded", run3.GetProperty("status").GetString());
        Assert.Equal(3, run3.GetProperty("rowsRead").GetInt64());
        Assert.Equal(3, run3.GetProperty("rowsWritten").GetInt64());
        await AssertTargetRowsAsync(new Dictionary<int, string> { [2] = "Bobby", [3] = "Caroline", [5] = "Eve" });
    }

    private async Task<JsonElement> TriggerAndWaitAsync()
    {
        var triggerResponse = await _client.PostAsync($"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        triggerResponse.EnsureSuccessStatusCode();
        var triggerBody = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        // This replication has exactly one table mapping, so "runIds" always has a single element —
        // a trigger now enqueues one Primary pass per mapping.
        var runId = Guid.Parse(triggerBody.GetProperty("runIds").EnumerateArray().Single().GetString()!);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
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

        throw new TimeoutException($"Run {runId} did not reach a terminal status within 30s.");
    }

    private async Task AssertTargetRowsAsync(Dictionary<int, string> expected)
    {
        await using var tgtConnection = OpenDatabaseConnection(TargetServerConnectionString);
        await using var cmd = tgtConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Name FROM dbo.[{_targetTable}];";
        await using var reader = await cmd.ExecuteReaderAsync();
        var actual = new Dictionary<int, string>();
        while (await reader.ReadAsync())
            actual[reader.GetInt32(0)] = reader.GetString(1);

        Assert.Equal(expected, actual);
    }

    private SqlConnection OpenDatabaseConnection(string serverConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
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

    private async Task CreateConnectionAsync(string name, string host, int port) =>
        (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = DriverIds.MsSql,
            Host = host,
            Port = port,
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

        // The PUT saves the mapping without introspecting — phase 90 captures the cache from the
        // columns a client sends, and a hand-built TableMappingConfig sends none, leaving phase 91's
        // readers and writers nothing to run from. This is the Refresh metadata endpoint, which reads
        // both catalogs server-side.
        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }
}
