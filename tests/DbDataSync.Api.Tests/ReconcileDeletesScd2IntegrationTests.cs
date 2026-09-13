using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 129's SCD2 ending of the same delete-diff sweep <see cref="ReconcileDeletesIntegrationTests"/>
/// covers for a plain writer, driven the identical way — real HTTP, a real spawned
/// DbDataSync.TaskRunner worker, a real SQL Server. A sibling class rather than more facts on that one:
/// the mapping's own Change Processing writer has to be <c>Scd2</c> for this whole test, which needs a
/// target table shape (<c>DS_VersionKey</c>/<c>DS_ValidFrom</c>/<c>DS_ValidTo</c>/<c>DS_IsCurrent</c>)
/// that class's shared fixture never creates, so its <c>InitializeAsync</c> could not be reused as-is —
/// the same reasoning <see cref="Scd2NaturalKeyIntegrationTests"/> gives for being its own class rather
/// than more methods on <c>RunExecutorIntegrationTests</c>.
/// <para>
/// The mapping's <c>Reconcile</c> is enabled with no explicit <c>Writer</c> override — this proves
/// <see cref="PipelineResolution.ReconcileWriterKind"/>'s new default (<c>KeyReconcileScd2Close</c> when
/// the mapping's own writer is <c>Scd2</c>) is what actually reaches the worker through
/// <c>ReconcileService</c>, not only what a unit test of <c>PipelineResolution</c> can see in isolation.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReconcileDeletesScd2IntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
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
    private readonly string _databaseName = $"DbDataSyncReconcileScd2Test_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"rc-scd2-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName;
    private const string TargetTable = "TgtHist";

    public ReconcileDeletesScd2IntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        _replicationName = $"rc-scd2-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
        }

        await using var db = await OpenDatabaseAsync();
        await ExecuteAsync(db, "CREATE TABLE dbo.[Src] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(db, "INSERT INTO dbo.[Src] (Id, Name) VALUES (1, 'One'), (2, 'Two'), (3, 'Three');");
        // No Tgt table created here, deliberately — CreateTargetTableIfMissing provisions the real
        // SCD2 shape (DS_VersionKey/DS_ValidFrom/DS_ValidTo/DS_IsCurrent) the first time a pass runs,
        // the same fixture posture Scd2NaturalKeyIntegrationTests uses for the identical reason.

        SetSecretEnvVar(_connectionName, "DbDataSync_Test_Pw1");
        await CreateConnectionAsync();
        await CreateReplicationAsync();
        await CreateMappingAsync();

        // Reconcile is enabled only now, in a second PUT of the replication, *after* the mapping's
        // source columns are cached (CreateMappingAsync's refresh-metadata, above). SaveTableMapping
        // validates an enabled Reconcile against the mapping's cached primary key (ConfigValidation.
        // ValidateKeyReconcilePairing) — a fresh mapping's cache is empty until refresh-metadata runs,
        // so enabling Reconcile any earlier than this would fail that check on the mapping PUT itself.
        // SaveReplicationTask, unlike SaveTableMapping, does not re-validate Reconcile pairing on its
        // own, so this PUT succeeds regardless — the real check that matters is whether the sweep
        // below actually runs, not whether this save was validated.
        await EnableReconcileAsync();

        // Seed the target through a real Primary pass — this both provisions TgtHist and caches its
        // columns (including the historized ones), and gives the reconcile sweep below a real "3 open
        // versions" starting state, the same role phase 124's own fixture gives its seeding pass.
        var primary = await ReadRunIdsAsync(await _client.PostAsync($"/api/replications/{_replicationName}/runs", Empty()));
        AssertAllSucceeded(await Task.WhenAll(primary.Select(PollUntilTerminalAsync)));
        Assert.Equal(3, await OpenVersionCountAsync());
    }

    public async Task DisposeAsync()
    {
        SetSecretEnvVar(_connectionName, null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    /// <summary>
    /// The whole point of the phase, driven through the real trigger rather than a hand-built work
    /// item: deleting a source row and POSTing <c>reconcile-deletes</c> closes that key's open version
    /// — <c>DS_IsCurrent = false</c>, a populated <c>DS_ValidTo</c> — never deletes the row, leaves an
    /// untouched key's version open, and is reported by run history the identical way phase 124's
    /// plain-delete case is.
    /// </summary>
    [Fact]
    public async Task ReconcileDeletes_ClosesTheVersionOfTheRowDeletedAtTheSource()
    {
        await using (var db = await OpenDatabaseAsync())
            await ExecuteAsync(db, "DELETE FROM dbo.[Src] WHERE Id = 1;");

        var response = await PostReconcileAsync(new FullSegment());
        var runIds = await ReadRunIdsAsync(response);
        var run = Assert.Single(await Task.WhenAll(runIds.Select(PollUntilTerminalAsync)));

        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.Equal("ReconcileDeletes", run.GetProperty("runKind").GetString());

        // The row is still there — closed, not gone. Phase 129's entire reason to exist.
        Assert.Equal(3, await RowCountAsync());
        Assert.Equal(2, await OpenVersionCountAsync());
        var (isCurrent, validToIsSet) = await OpenStateAsync(1);
        Assert.False(isCurrent, "the deleted key's version should have been closed");
        Assert.True(validToIsSet, "a closed version must have DS_ValidTo populated");

        var untouched = await OpenStateAsync(2);
        Assert.True(untouched.IsCurrent);
        Assert.False(untouched.ValidToIsSet);

        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/runs?kind=ReconcileDeletes&limit=50", JsonOptions);
        Assert.Single(history.GetProperty("runs").EnumerateArray());
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static StringContent Empty() => new("", Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostReconcileAsync(BatchReloadSegment segment, string mappingName = "map")
    {
        var body = $$"""{ "segments": [{{SegmentSerializer.Serialize(segment)}}] }""";
        return _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/{mappingName}/reconcile-deletes",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static async Task<List<Guid>> ReadRunIdsAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
    }

    private static void AssertAllSucceeded(IReadOnlyList<JsonElement> runs)
    {
        var failures = runs
            .Where(r => r.GetProperty("status").GetString() != "Succeeded")
            .Select(r => $"{r.GetProperty("runId").GetString()}: {r.GetProperty("status").GetString()} — {r.GetProperty("errorSummary").GetString()}")
            .ToList();
        Assert.True(failures.Count == 0, "Expected every run to succeed, but got:\n" + string.Join("\n", failures));
    }

    private async Task<JsonElement> PollUntilTerminalAsync(Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/runs/{runId}");
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (run.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled")
                    return run;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not reach a terminal status within 90s.");
    }

    private async Task<SqlConnection> OpenDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<int> RowCountAsync()
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{TargetTable}];";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> OpenVersionCountAsync()
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{TargetTable}] WHERE {HistorizedColumns.IsCurrent} = 1;";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(bool IsCurrent, bool ValidToIsSet)> OpenStateAsync(int id)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {HistorizedColumns.IsCurrent}, {HistorizedColumns.ValidTo} " +
            $"FROM dbo.[{TargetTable}] WHERE Id = {id};";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No row for Id = {id} in {TargetTable}.");
        return (reader.GetBoolean(0), !await reader.IsDBNullAsync(1));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateConnectionAsync() =>
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

    /// <summary>The mapping's own Change Processing writer is Scd2, and Reconcile is enabled with no
    /// explicit Writer override — the case that only works once ReconcileService resolves the writer
    /// Kind per mapping via <see cref="PipelineResolution.ReconcileWriterKind"/> instead of a fixed
    /// constant.</summary>
    private async Task CreateReplicationAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "Scd2" },
            },
            Provisioning = new ProvisioningConfig { CreateTargetTableIfMissing = true },
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task EnableReconcileAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "Scd2" },
            },
            Provisioning = new ProvisioningConfig { CreateTargetTableIfMissing = true },
            // No explicit Writer here — proving PipelineResolution.ReconcileWriterKind's default
            // (KeyReconcileScd2Close, because the mapping's own writer above is Scd2) is what actually
            // reaches ReconcileService and the worker, not merely what a unit test can see in isolation.
            Reconcile = new ReconcileConfig { Enabled = true },
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateMappingAsync()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map", new TableMappingConfig
        {
            Name = "map",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Src" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = TargetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // Populates the source side of the phase-90/91 column cache. The target does not exist yet —
        // refresh-metadata leaves that side's cache untouched rather than failing (see
        // MappingMetadataService), and the first Primary pass below is what actually provisions it and
        // caches the historized shape.
        (await _client.PostAsync($"/api/replications/{_replicationName}/table-mappings/map/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    private void SetSecretEnvVar(string connectionName, string? password) =>
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(connectionName)), password);
}
