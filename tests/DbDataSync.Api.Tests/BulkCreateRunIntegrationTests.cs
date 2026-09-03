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
/// The point of phase 95, against real databases: a mapping created in bulk runs, and nobody touched
/// it in between.
/// <para>
/// Every other integration test in this project builds its mapping by hand and then calls
/// <c>refresh-metadata</c> before it can run — see the note in
/// <see cref="RunLifecycleIntegrationTests"/> explaining why it has to. **This one deliberately calls
/// neither.** It ticks tables the way the bulk screen does, triggers a pass, and expects rows to move.
/// If capture or auto-mapping regresses, this fails with the real exception an operator would have
/// got: <c>MetadataNotCachedException</c>, or "ColumnMappings must be specified to stage changes".
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BulkCreateRunIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client;
    private readonly SecretStore _secrets;

    // Two databases, one table name. Bulk create mirrors the source's schema and table onto the
    // target, so "dbo.Existing" has to mean a different table on each side — which is the arrangement
    // the screen assumes anyway: one source database replicated into one warehouse.
    private readonly string _sourceDb = $"DbDataSyncBulkSrc_{Guid.NewGuid():N}";
    private readonly string _targetDb = $"DbDataSyncBulkTgt_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"src-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"tgt-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"repl-{Guid.NewGuid():N}";

    /// <summary>Mapped onto a target table that is already there.</summary>
    private const string Existing = "Existing";

    /// <summary>Mapped onto a target that does not exist until provisioning creates it — the ordinary
    /// case for a mapping created from a source table, and the one where what gets created *is* what
    /// auto-mapping mapped.</summary>
    private const string Provisioned = "Provisioned";

    public BulkCreateRunIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            foreach (var db in new[] { _sourceDb, _targetDb })
                await ExecuteAsync(bootstrap, $"CREATE DATABASE [{db}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_sourceDb}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        await using (var source = await OpenAsync(_sourceDb))
        {
            foreach (var table in new[] { Existing, Provisioned })
            {
                await ExecuteAsync(source,
                    $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
                await ExecuteAsync(source, $"ALTER TABLE dbo.[{table}] ENABLE CHANGE_TRACKING;");
                await ExecuteAsync(source, $"INSERT INTO dbo.[{table}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
            }
        }

        await using (var target = await OpenAsync(_targetDb))
        {
            await ExecuteAsync(target,
                $"CREATE TABLE dbo.[{Existing}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        }

        // As RunLifecycleIntegrationTests: the spawned TaskRunner is a real child process with its own
        // SecretStore, and inherits this process's environment.
        SetSecretEnvVar(_srcConnectionName, "DbDataSync_Test_Pw1");
        SetSecretEnvVar(_tgtConnectionName, "DbDataSync_Test_Pw1");

        await CreateConnectionAsync(_srcConnectionName, _sourceDb);
        await CreateConnectionAsync(_tgtConnectionName, _targetDb);
        await CreateReplicationAsync();
    }

    public async Task DisposeAsync()
    {
        ClearSecretEnvVar(_srcConnectionName);
        ClearSecretEnvVar(_tgtConnectionName);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            await ExecuteAsync(connection, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            await ExecuteAsync(connection, $"DROP DATABASE [{db}];");
        }
    }

    /// <summary>
    /// Ticks a table on a replication and triggers a pass. **No <c>refresh-metadata</c> call and no
    /// hand-written column mappings anywhere in this test** — that absence is the assertion.
    /// </summary>
    [Fact]
    public async Task ABulkCreatedMapping_RunsWithoutAnybodyOpeningIt()
    {
        var result = await BulkCreateAsync(Existing);
        Assert.Equal([$"dbo.{Existing}"], result.GetProperty("created").EnumerateArray().Select(e => e.GetString()));

        var run = await TriggerAndAwaitAsync($"dbo.{Existing}");

        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(2, run.GetProperty("rowsWritten").GetInt64());
        Assert.Equal(2, await CountAsync(_targetDb, Existing));
    }

    /// <summary>
    /// The commoner case, and the one where auto-mapping decides more than which columns move:
    /// <c>ProvisioningService</c> builds its <c>CREATE TABLE</c> from the column mappings, so a
    /// bulk-created mapping whose target does not exist creates the table its auto-map described. A
    /// mapping created with no columns would have created a table with none.
    /// </summary>
    [Fact]
    public async Task ABulkCreatedMappingWhoseTargetDoesNotExist_ProvisionsItFromTheColumnsItAutoMapped()
    {
        var result = await BulkCreateAsync(Provisioned);

        // Reported, not hidden: the target genuinely could not be read, because it is not there yet.
        var note = Assert.Single(result.GetProperty("notes").EnumerateArray().ToList());
        Assert.Equal("target", note.GetProperty("side").GetString());

        var run = await TriggerAndAwaitAsync($"dbo.{Provisioned}");

        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal(2, await CountAsync(_targetDb, Provisioned));
        Assert.Equal(["Id", "Name"], await ColumnsOfAsync(_targetDb, Provisioned));
    }

    private async Task<JsonElement> BulkCreateAsync(string table)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/bulk",
            new { tables = new[] { new { schema = "dbo", table } } }, JsonOptions);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Triggers the replication and waits for the pass belonging to <paramref name="mappingName"/>. A
    /// trigger enqueues one pass per mapping, and this fixture accumulates mappings across its tests,
    /// so the run to watch is picked by mapping rather than by being the only one.
    /// </summary>
    private async Task<JsonElement> TriggerAndAwaitAsync(string mappingName)
    {
        var trigger = await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        trigger.EnsureSuccessStatusCode();

        var runIds = (await trigger.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("runIds").EnumerateArray().Select(e => e.GetString()!).ToList();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var runId in runIds)
            {
                var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
                if (detail.GetProperty("mappingName").GetString() != mappingName)
                    continue;

                var status = detail.GetProperty("status").GetString();
                if (status is "Succeeded" or "Failed")
                    return detail;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"No pass for '{mappingName}' finished within 60s.");
    }

    private async Task<SqlConnection> OpenAsync(string database)
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<int> CountAsync(string database, string table)
    {
        await using var connection = await OpenAsync(database);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}];";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> ColumnsOfAsync(string database, string table)
    {
        await using var connection = await OpenAsync(database);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.name FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.name = @table ORDER BY c.column_id;
            """;
        cmd.Parameters.AddWithValue("@table", table);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
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

    private async Task CreateConnectionAsync(string name, string database) =>
        (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = database,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    /// <summary>
    /// Both endpoints on the replication and nothing on the mappings, which is the arrangement bulk
    /// creation assumes: a mapping it creates states only its tables.
    /// </summary>
    private async Task CreateReplicationAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = true,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = _srcConnectionName, Database = _sourceDb },
                Target = new EndpointRef { ConnectionName = _tgtConnectionName, Database = _targetDb },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            Provisioning = new ProvisioningConfig { CreateTargetTableIfMissing = true },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
}
