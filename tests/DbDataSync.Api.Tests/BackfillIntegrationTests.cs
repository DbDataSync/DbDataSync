using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The backfill trigger endpoint driven end to end through real HTTP, a real spawned
/// DbDataSync.TaskRunner worker, and a real SQL Server — proving the properties the whole per-mapping
/// run model was built for, rather than asserting them against the stores directly (which is what
/// WorkQueueStoreTests already does, and which can't observe two processes racing).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackfillIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private const int RowsPerTable = 9;

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
    private readonly string _databaseName = $"DbDataSyncBackfillTest_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"bf-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName;

    public BackfillIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        _replicationName = $"bf-{Guid.NewGuid():N}";
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

        await using var db = await OpenDatabaseAsync();
        foreach (var i in new[] { 1, 2 })
        {
            await ExecuteAsync(db, $"CREATE TABLE dbo.[Src_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
            await ExecuteAsync(db, $"ALTER TABLE dbo.[Src_{i}] ENABLE CHANGE_TRACKING;");
            await ExecuteAsync(db, $"CREATE TABLE dbo.[Tgt_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

            var values = string.Join(", ", Enumerable.Range(1, RowsPerTable).Select(r => $"({r}, 'Row{i}_{r}')"));
            await ExecuteAsync(db, $"INSERT INTO dbo.[Src_{i}] (Id, Name) VALUES {values};");
        }

        SetSecretEnvVar(_connectionName, "DbDataSync_Test_Pw1");
        await CreateConnectionAsync();
        await CreateReplicationAsync();
        await CreateMappingAsync(1);
        await CreateMappingAsync(2);
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
    /// The direct proof of the coexistence requirement the Phase 8 foundation was built for: a
    /// replication's ordinary scheduled sync and an on-demand reload are separate units of work with
    /// separate locks, so triggering both at once is normal operation, not contention.
    /// <para>
    /// Both pipelines converge on "the target matches the source" — the source doesn't change during
    /// the test — so the interleaving between them doesn't make the final state ambiguous.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrimaryAndBackfill_TriggeredConcurrently_BothSucceed()
    {
        var primaryTask = _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        var backfillTask = PostBackfillAsync("map-1", new FullSegment());

        var primaryRunIds = await ReadRunIdsAsync(await primaryTask);
        var backfillRunIds = await ReadRunIdsAsync(await backfillTask);

        Assert.Equal(2, primaryRunIds.Count);   // one Primary pass per table mapping
        Assert.Single(backfillRunIds);

        var runs = await Task.WhenAll(primaryRunIds.Concat(backfillRunIds).Select(PollUntilTerminalAsync));
        AssertAllSucceeded(runs);

        // The Backfill row is distinguishable in history, which is what the SPA's badge renders from.
        var backfillRun = runs.Single(r => r.GetProperty("runId").GetString() == backfillRunIds[0].ToString());
        Assert.Equal("Backfill", backfillRun.GetProperty("runKind").GetString());
        Assert.Equal("map-1", backfillRun.GetProperty("mappingName").GetString());
        Assert.Equal("full", backfillRun.GetProperty("segmentLabel").GetString());

        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_2"));
    }

    /// <summary>
    /// Two triggers for the same segment collapse into one unit of work rather than reloading it
    /// twice. Both requests are in flight before either can plausibly have been claimed — the handler
    /// enqueues before it so much as asks for a worker, and spawning one is far slower than the
    /// second request's own enqueue.
    /// </summary>
    [Fact]
    public async Task TwoIdenticalBackfillTriggers_CollapseIntoOneRun()
    {
        var segment = new RangeSegment("Id", "1", "5");
        var responses = await Task.WhenAll(PostBackfillAsync("map-2", segment), PostBackfillAsync("map-2", segment));

        var first = await ReadRunIdsAsync(responses[0]);
        var second = await ReadRunIdsAsync(responses[1]);
        Assert.Equal(first, second);

        AssertAllSucceeded([await PollUntilTerminalAsync(first.Single())]);

        // Phase 104: this endpoint returns a page ({ runs, nextCursor }), not a bare array.
        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/runs?kind=Backfill&limit=50", JsonOptions);
        Assert.Single(history.GetProperty("runs").EnumerateArray());

        // Half-open [1, 5) — reloaded exactly once, and nothing outside it was touched.
        Assert.Equal(4, await CountAsync("Tgt_2"));
    }

    /// <summary>
    /// An Auto segment is expanded server-side against the source's real value range, and each
    /// resulting bucket becomes its own independently-scheduled run — so one submission legitimately
    /// returns many RunIds.
    /// </summary>
    [Fact]
    public async Task AutoSegmentBackfill_ExpandsIntoOneRunPerBucket_AndEveryRowLandsOnce()
    {
        var response = await PostBackfillAsync("map-1", new AutoSegment("Id", 3));
        var runIds = await ReadRunIdsAsync(response);

        Assert.Equal(3, runIds.Count);
        var runs = await Task.WhenAll(runIds.Select(PollUntilTerminalAsync));
        AssertAllSucceeded(runs);

        // Distinct, non-overlapping bucket labels — one run per bucket, not three runs of one bucket.
        var labels = runs.Select(r => r.GetProperty("segmentLabel").GetString()).ToList();
        Assert.Equal(3, labels.Distinct().Count());

        // The buckets have to tile the range and cover MAX for every row to land exactly once.
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));
        Assert.Equal(RowsPerTable, runs.Sum(r => r.GetProperty("rowsRead").GetInt64()));
    }

    /// <summary>
    /// The Monitoring screen's "Batch reload" card reads <c>/backfills</c>: one row per backfill,
    /// rolled up across its segment runs, with a catalog-statistics estimate of the whole table as
    /// the denominator (<c>sys.partitions</c>, never a COUNT(*)).
    /// </summary>
    [Fact]
    public async Task Backfills_RollsUpTheSegmentsAndCarriesACatalogEstimate()
    {
        var runIds = await ReadRunIdsAsync(await PostBackfillAsync("map-1", new AutoSegment("Id", 3)));
        Assert.Equal(3, runIds.Count);

        // Before the segments finish: the batch exists, knows its planned segment count, and already
        // carries the estimate read once at enqueue.
        var midway = await GetLatestBackfillAsync();
        Assert.Equal("map-1", midway.GetProperty("mappingName").GetString());
        Assert.Equal(3, midway.GetProperty("segmentCount").GetInt32());
        Assert.Equal(RowsPerTable, midway.GetProperty("estimatedRows").GetInt64());
        Assert.Null(midway.GetProperty("estimateCaveat").GetString());

        AssertAllSucceeded(await Task.WhenAll(runIds.Select(PollUntilTerminalAsync)));

        var done = await GetLatestBackfillAsync();
        Assert.Equal("Completed", done.GetProperty("state").GetString());
        Assert.Equal(3, done.GetProperty("segmentsSucceeded").GetInt32());
        Assert.Equal(RowsPerTable, done.GetProperty("rowsCopied").GetInt64());
    }

    [Fact]
    public async Task Backfill_ForAnUnknownMapping_Is404()
    {
        var response = await PostBackfillAsync("no-such-mapping", new FullSegment());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Rejected up front rather than queued: a segment naming a column that doesn't exist
    /// can only ever fail, and the caller is right there to be told why.</summary>
    [Fact]
    public async Task Backfill_WithASegmentColumnThatDoesNotExist_Is400()
    {
        var response = await PostBackfillAsync("map-1", new AutoSegment("NoSuchColumn", 2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("NoSuchColumn", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Backfill_WithNoSegments_Is400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{_replicationName}/mappings/map-1/backfill",
            new { readerKind = "MsSqlBatchReload", segments = Array.Empty<object>() },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private Task<HttpResponseMessage> PostBackfillAsync(string mappingName, BatchReloadSegment segment)
    {
        // Serialized through SegmentSerializer so the polymorphic "mode" discriminator on the wire is
        // built exactly the way every other producer of segment JSON builds it.
        var body = $$"""
            {
              "readerKind": "{{MsSqlKinds.BatchReload}}",
              "cacheKind": "{{MsSqlKinds.StagingTable}}",
              "writerKind": "{{MsSqlKinds.MergeReconcile}}",
              "segments": [{{SegmentSerializer.Serialize(segment)}}]
            }
            """;
        return _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/{mappingName}/backfill",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    /// <summary>The MSSQL driver's Kind strings, repeated here rather than referenced: these tests
    /// exercise the HTTP contract, where a Kind is just a string a caller got from the capabilities
    /// endpoint — taking a project reference on the driver would test something weaker than that.</summary>
    private static class MsSqlKinds
    {
        public const string BatchReload = "MsSqlBatchReload";
        public const string StagingTable = "MsSqlStagingTable";
        public const string MergeReconcile = "MsSqlMergeReconcile";
    }

    private static async Task<List<Guid>> ReadRunIdsAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new Exception($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

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

    private async Task<JsonElement> GetLatestBackfillAsync()
    {
        var batches = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/backfills", JsonOptions);
        var list = batches.EnumerateArray().ToList();
        Assert.NotEmpty(list);
        return list[0];
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

    private async Task<int> CountAsync(string table)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}];";
        return (int)(await cmd.ExecuteScalarAsync())!;
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
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateReplicationAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            // Deliberately configured for *incremental* sync: a backfill of this replication must
            // override the pipeline entirely, which is the whole point of the per-item Kinds.
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = MsSqlKinds.StagingTable },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateMappingAsync(int index)
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map-{index}", new TableMappingConfig
        {
            Name = $"map-{index}",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = $"Src_{index}" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = $"Tgt_{index}" }],
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
            $"/api/replications/{_replicationName}/table-mappings/map-{index}/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    private void SetSecretEnvVar(string connectionName, string? password) =>
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(connectionName)), password);
}
