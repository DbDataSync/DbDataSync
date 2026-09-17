using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Controllers;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The bulk load trigger endpoint driven end to end through real HTTP, a real spawned
/// DbDataSync.TaskRunner worker, and a real SQL Server — proving the properties the whole per-mapping
/// run model was built for, rather than asserting them against the stores directly (which is what
/// WorkQueueStoreTests already does, and which can't observe two processes racing).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BulkLoadIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
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
    private readonly WorkQueueStore _workQueueStore;
    private readonly TaskRunStore _taskRunStore;
    private readonly string _databaseName = $"DbDataSyncBulkLoadTest_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"bf-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName;

    public BulkLoadIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        _workQueueStore = factory.Services.GetRequiredService<WorkQueueStore>();
        _taskRunStore = factory.Services.GetRequiredService<TaskRunStore>();
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
    /// <para>
    /// **Warms up both mappings first, deliberately.** Phase 134 made a mapping's very first Primary
    /// pass auto-request a Bulk Load too (for a position-capturing reader, which Change Tracking is) —
    /// racing that against this test's own explicit reload is a completely different scenario (the
    /// subject of phase 143, not this test), and without a warm-up this test was quietly exercising it
    /// by accident: a brand-new mapping's first pass and an on-demand reload of the identical segment,
    /// not the already-bootstrapped-mapping-vs-reload coexistence this test's name and doc comment
    /// actually describe. See <see cref="ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals"/>
    /// for that scenario, deliberately, instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed()
    {
        var warmupRunIds = await ReadRunIdsAsync(await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json")));
        Assert.Equal(2, warmupRunIds.Count);
        AssertAllSucceeded(await Task.WhenAll(warmupRunIds.Select(PollUntilTerminalAsync)));
        await _client.WaitForLoadToCompleteAsync(_replicationName, "map-1");
        await _client.WaitForLoadToCompleteAsync(_replicationName, "map-2");
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_2"));

        // The test's real subject: both mappings already have a live watermark, so this Primary
        // trigger is a genuinely incremental pass for each — no auto-triggered Bulk Load, no collision
        // with the explicit reload below, exactly the coexistence this test is named for.
        var primaryTask = _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        var bulkLoadTask = PostBulkLoadAsync("map-1", new FullSegment());

        var primaryRunIds = await ReadRunIdsAsync(await primaryTask);
        var bulkLoadRunIds = await ReadRunIdsAsync(await bulkLoadTask);

        Assert.Equal(2, primaryRunIds.Count);   // one Primary pass per table mapping
        Assert.Single(bulkLoadRunIds);

        var runs = await Task.WhenAll(primaryRunIds.Concat(bulkLoadRunIds).Select(PollUntilTerminalAsync));
        AssertAllSucceeded(runs);

        // The BulkLoad row is distinguishable in history, which is what the SPA's badge renders from.
        var bulkLoadRun = runs.Single(r => r.GetProperty("runId").GetString() == bulkLoadRunIds[0].ToString());
        Assert.Equal("BulkLoad", bulkLoadRun.GetProperty("runKind").GetString());
        Assert.Equal("map-1", bulkLoadRun.GetProperty("mappingName").GetString());
        Assert.Equal("full", bulkLoadRun.GetProperty("segmentLabel").GetString());

        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_2"));
    }

    /// <summary>
    /// Phase 143's own scenario, deliberately — what
    /// <see cref="PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed"/> exercised by accident before
    /// its own warm-up was added: a brand-new mapping's first-ever Primary pass (Change Tracking, a
    /// position-capturing reader) auto-requests a Bulk Load for the same instant an operator's own
    /// explicit reload requests the identical segment. Two independent requests, not one that happens
    /// to arrive twice.
    /// <para>
    /// **Which one loses is a genuine race, not a reliable outcome — found the hard way, on a real CI
    /// run, 2026-09-17.** An earlier version of this comment claimed the explicit trigger "consistently
    /// wins, since it enqueues immediately on the HTTP request rather than waiting on a worker to claim
    /// and start processing the Primary pass first" — true of the *enqueue* step alone, but
    /// <see cref="RunExecutor.ExecuteWorkerAsync"/> runs the ChangeProcessing and BulkLoad lanes
    /// *concurrently* on the same spawned worker process (<c>Task.WhenAll(change, bulkLoad)</c>), from
    /// the moment it starts. Once both work items are enqueued (both near-instant, from independent HTTP
    /// handlers), the real race is between the BulkLoad lane finishing its own read+write before the
    /// ChangeProcessing lane's Primary pass gets through opening a source connection and capturing its
    /// own position — two comparable, real SQL round trips on either side, whose relative speed a CI
    /// runner's own load can and does flip. Both outcomes are equally correct: either the auto-request
    /// finds the explicit reload's row still there and fails cleanly with
    /// <see cref="DbDataSync.State.RunFailureKinds.ConcurrentLoadInProgress"/> (not merged into the
    /// winner's own outcome, not stranding the mapping's `ReadHold` — self-heals on the next scheduled
    /// pass with nothing left to race), or it finds nothing left to collide with and simply succeeds as
    /// an ordinary, non-colliding initial load. This test asserts both branches explicitly rather than
    /// assuming only one can happen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals()
    {
        var primaryTask = _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        var bulkLoadTask = PostBulkLoadAsync("map-1", new FullSegment());

        var primaryRunIds = await ReadRunIdsAsync(await primaryTask);
        var bulkLoadRunIds = await ReadRunIdsAsync(await bulkLoadTask);
        Assert.Equal(2, primaryRunIds.Count);
        Assert.Single(bulkLoadRunIds);

        var primaryRuns = await Task.WhenAll(primaryRunIds.Select(PollUntilTerminalAsync));
        var bulkLoadRun = await PollUntilTerminalAsync(bulkLoadRunIds[0]);

        // The explicit reload wins and genuinely moves the data — unaffected by the race.
        Assert.Equal("Succeeded", bulkLoadRun.GetProperty("status").GetString());
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));

        // map-1's own Primary pass is the one that auto-requested the identical segment — whether it
        // lost the race is genuinely up to timing (see this test's own doc comment), so both outcomes
        // are checked explicitly rather than assuming only the "lost" branch can happen.
        var map1Primary = Assert.Single(primaryRuns, r => r.GetProperty("mappingName").GetString() == "map-1");
        var map1Status = map1Primary.GetProperty("status").GetString();
        if (map1Status == "Failed")
        {
            // The intended branch: the auto-request found the explicit reload's row still there.
            Assert.Equal("ConcurrentLoadInProgress", map1Primary.GetProperty("failureKind").GetString());
            Assert.Contains("already in progress", map1Primary.GetProperty("errorSummary").GetString());
        }
        else
        {
            // The explicit reload finished (enqueue *and* run) before the auto-request ever reached
            // its own collision check — nothing left to collide with, so this is just an ordinary,
            // non-colliding initial load. Equally correct; not the shape this test exists to name, but
            // not a failure of anything this test is actually checking either.
            Assert.Equal("Succeeded", map1Status);
        }

        // map-2 was never part of the race — its own auto-trigger had nothing to collide with.
        var map2Primary = Assert.Single(primaryRuns, r => r.GetProperty("mappingName").GetString() == "map-2");
        Assert.Equal("Succeeded", map2Primary.GetProperty("status").GetString());

        // The other, still-open half of this same window: a manual re-trigger while map-2's own
        // (unrelated) Bulk Load is still genuinely Loading. The endpoint enqueues a Primary pass per
        // mapping regardless of ReadHold — deliberately, per SchedulerService.FilterHeld's own doc
        // comment, only the scheduler's automatic due-check honours the hold — so this used to resolve
        // to Changes intent against a watermark that was never made live, and crash with a bare
        // ArgumentNullException from MsSqlChangeTrackingReader. It now fails cleanly instead.
        //
        // Confirmed still Loading first, rather than assumed: map-2's own real Bulk Load is a separate,
        // independently-timed work item from the Primary pass just polled to terminal above, and racing
        // the retrigger against it blind — fire immediately and hope the real load is still running —
        // is exactly the kind of timing assumption that holds on one machine and not another (this one
        // failed on CI, never locally, until this fix). Polling for the Hold narrows the window to a
        // single GET-then-POST instead of however long everything above happened to take.
        var map2StillLoading = await PollUntilHoldAsync("map-2", ReadHold.Loading, TimeSpan.FromSeconds(5));
        if (map2StillLoading)
        {
            var raceRunIds = await ReadRunIdsAsync(await _client.PostAsync(
                $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json")));
            var raceRuns = await Task.WhenAll(raceRunIds.Select(PollUntilTerminalAsync));
            var map2Race = Assert.Single(raceRuns, r => r.GetProperty("mappingName").GetString() == "map-2");
            Assert.Equal("Failed", map2Race.GetProperty("status").GetString());
            Assert.Equal("MappingStillLoading", map2Race.GetProperty("failureKind").GetString());
            Assert.Contains("still loading", map2Race.GetProperty("errorSummary").GetString());
        }
        // Else: map-2's own load finished before this test could ever observe it Loading — the window
        // this half of the test exercises had already closed on its own, an environment-speed accident
        // rather than anything to assert about. Covered deterministically instead by
        // RunExecutorTests.ExecuteWorkerAsync_AManualTriggerWhileStillLoading_FailsCleanly_BeforeAnyConnectionIsOpened,
        // which sets the hold directly rather than racing a real Bulk Load for it.

        await _client.WaitForLoadToCompleteAsync(_replicationName, "map-2");

        // Self-healing: map-1 still has no live watermark (its own attempt never promoted one), so its
        // next scheduled pass tries the whole capture-and-request sequence again — and this time wins
        // cleanly, with nothing left racing it.
        var retryRunIds = await ReadRunIdsAsync(await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json")));
        AssertAllSucceeded(await Task.WhenAll(retryRunIds.Select(PollUntilTerminalAsync)));
        await _client.WaitForLoadToCompleteAsync(_replicationName, "map-1");
        Assert.Equal(RowsPerTable, await CountAsync("Tgt_1"));
    }

    /// <summary>
    /// Follow-up to phase 143: a mapping with a multi-segment <c>DefaultSegmenting</c> can win some of
    /// its own auto-triggered initial load's segments' <c>WorkQueue</c> races and lose others — a
    /// narrower version of the single-segment case phase 143 fixed. Without the rollback this covers,
    /// the segment(s) that won before the loss (segment 1 here) would be left durably enqueued as real
    /// work under a batch whose own <c>SegmentCount</c> (2) could never be satisfied by its own
    /// completions, since nothing ever enqueues the segment that lost — the batch would report
    /// <c>BulkLoadState.Running</c> forever. It now rolls back cleanly instead: segment 1 is cancelled,
    /// and the batch itself is removed, leaving no trace, the same as the single-segment case leaves
    /// none.
    /// </summary>
    [Fact]
    public async Task AMultiSegmentInitialLoad_ThatPartlyCollides_RollsBackWhatItAlreadyEnqueued()
    {
        await SetUpSegmentedMappingAsync();
        var segment2 = new RangeSegment("Id", "5", "10");

        // Simulates "an operator's own reload of the identical segment scheme is already in flight" for
        // segment 2 alone — pre-seeded directly rather than raced over HTTP, so which segment collides
        // is deterministic rather than a timing gamble.
        _workQueueStore.Enqueue(
            _replicationName, RunKind.BulkLoad, "map-3", segment2.Describe(), SegmentSerializer.Serialize(segment2));

        var primaryRunIds = await ReadRunIdsAsync(await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json")));
        var primaryRuns = await Task.WhenAll(primaryRunIds.Select(PollUntilTerminalAsync));

        var map3Primary = Assert.Single(primaryRuns, r => r.GetProperty("mappingName").GetString() == "map-3");
        Assert.Equal("Failed", map3Primary.GetProperty("status").GetString());
        Assert.Equal("ConcurrentLoadInProgress", map3Primary.GetProperty("failureKind").GetString());

        // The batch this attempt created is gone — not lingering as a permanently-Running row nobody
        // will ever finish. GetHistory(BulkLoad) for map-3 has nothing beyond the pre-seeded segment-2
        // reload itself (which is unaffected by any of this — it was never this attempt's own work).
        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/runs?kind=BulkLoad&mappingName=map-3&limit=50", JsonOptions);
        var map3BulkLoadRuns = history.GetProperty("runs").EnumerateArray().ToList();
        Assert.DoesNotContain(map3BulkLoadRuns, r => r.GetProperty("status").GetString() == "Queued");

        // Segment 1 — the one this attempt actually got enqueued before segment 2 collided — was
        // cancelled, not left Pending/Queued forever.
        var segment1Runs = map3BulkLoadRuns.Where(r => r.GetProperty("segmentLabel").GetString() == "Id [1, 5)").ToList();
        Assert.All(segment1Runs, r => Assert.Equal("Cancelled", r.GetProperty("status").GetString()));
    }

    private async Task SetUpSegmentedMappingAsync()
    {
        await using var db = await OpenDatabaseAsync();
        await ExecuteAsync(db, "CREATE TABLE dbo.[Src_3] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(db, "ALTER TABLE dbo.[Src_3] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(db, "CREATE TABLE dbo.[Tgt_3] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        var values = string.Join(", ", Enumerable.Range(1, RowsPerTable).Select(r => $"({r}, 'Row3_{r}')"));
        await ExecuteAsync(db, $"INSERT INTO dbo.[Src_3] (Id, Name) VALUES {values};");

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map-3", new TableMappingConfig
        {
            Name = "map-3",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Src_3" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Tgt_3" }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
            // Empty means Full otherwise — this is the one thing that distinguishes this mapping from
            // CreateMappingAsync's own map-1/map-2: an initial load requests one segment per entry here.
            DefaultSegmenting = [new RangeSegment("Id", "1", "5"), new RangeSegment("Id", "5", "10")],
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/map-3/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Two triggers for the same segment collapse into one unit of work rather than reloading it
    /// twice. Both requests are in flight before either can plausibly have been claimed — the handler
    /// enqueues before it so much as asks for a worker, and spawning one is far slower than the
    /// second request's own enqueue.
    /// </summary>
    [Fact]
    public async Task TwoIdenticalBulkLoadTriggers_CollapseIntoOneRun()
    {
        var segment = new RangeSegment("Id", "1", "5");
        var responses = await Task.WhenAll(PostBulkLoadAsync("map-2", segment), PostBulkLoadAsync("map-2", segment));

        var first = await ReadRunIdsAsync(responses[0]);
        var second = await ReadRunIdsAsync(responses[1]);
        Assert.Equal(first, second);

        AssertAllSucceeded([await PollUntilTerminalAsync(first.Single())]);

        // Phase 104: this endpoint returns a page ({ runs, nextCursor }), not a bare array.
        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/runs?kind=BulkLoad&limit=50", JsonOptions);
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
    public async Task AutoSegmentBulkLoad_ExpandsIntoOneRunPerBucket_AndEveryRowLandsOnce()
    {
        var response = await PostBulkLoadAsync("map-1", new AutoSegment("Id", 3));
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
    /// The Monitoring screen's "Batch reload" card reads <c>/bulk-loads</c>: one row per bulk load,
    /// rolled up across its segment runs, with a catalog-statistics estimate of the whole table as
    /// the denominator (<c>sys.partitions</c>, never a COUNT(*)).
    /// </summary>
    [Fact]
    public async Task BulkLoads_RollsUpTheSegmentsAndCarriesACatalogEstimate()
    {
        var runIds = await ReadRunIdsAsync(await PostBulkLoadAsync("map-1", new AutoSegment("Id", 3)));
        Assert.Equal(3, runIds.Count);

        // Before the segments finish: the batch exists, knows its planned segment count, and already
        // carries the estimate read once at enqueue.
        var midway = await GetLatestBulkLoadAsync();
        Assert.Equal("map-1", midway.GetProperty("mappingName").GetString());
        Assert.Equal(3, midway.GetProperty("segmentCount").GetInt32());
        Assert.Equal(RowsPerTable, midway.GetProperty("estimatedRows").GetInt64());
        Assert.Null(midway.GetProperty("estimateCaveat").GetString());

        AssertAllSucceeded(await Task.WhenAll(runIds.Select(PollUntilTerminalAsync)));

        var done = await GetLatestBulkLoadAsync();
        Assert.Equal("Completed", done.GetProperty("state").GetString());
        Assert.Equal(3, done.GetProperty("segmentsSucceeded").GetInt32());
        Assert.Equal(RowsPerTable, done.GetProperty("rowsCopied").GetInt64());
    }

    [Fact]
    public async Task BulkLoad_ForAnUnknownMapping_Is404()
    {
        var response = await PostBulkLoadAsync("no-such-mapping", new FullSegment());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Rejected up front rather than queued: a segment naming a column that doesn't exist
    /// can only ever fail, and the caller is right there to be told why.</summary>
    [Fact]
    public async Task BulkLoad_WithASegmentColumnThatDoesNotExist_Is400()
    {
        var response = await PostBulkLoadAsync("map-1", new AutoSegment("NoSuchColumn", 2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("NoSuchColumn", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BulkLoad_WithNoSegments_Is400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{_replicationName}/mappings/map-1/bulk-load",
            new { readerKind = "MsSqlBatchReload", segments = Array.Empty<object>() },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private Task<HttpResponseMessage> PostBulkLoadAsync(string mappingName, BatchReloadSegment segment)
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
            $"/api/replications/{_replicationName}/mappings/{mappingName}/bulk-load",
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

    private async Task<JsonElement> GetLatestBulkLoadAsync()
    {
        var batches = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/bulk-loads", JsonOptions);
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

    /// <summary>True the moment a mapping's own read-state reports the given hold, false if it never
    /// does within <paramref name="timeout"/> — never throws, since "it finished before we could catch
    /// it" is an expected, non-erroneous outcome for a caller racing a real background load.</summary>
    private async Task<bool> PollUntilHoldAsync(string mappingName, ReadHold hold, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync(
                $"/api/replications/{_replicationName}/table-mappings/{mappingName}/read-state");
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
            if (state!.Hold == hold)
                return true;
            await Task.Delay(50);
        }

        return false;
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
            DriverType = DriverIds.MsSql,
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
            // Deliberately configured for *incremental* sync: a bulk load of this replication must
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
