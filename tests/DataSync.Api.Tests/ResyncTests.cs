using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using DataSync.Core.Sql;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The recovery for a source that discarded the history a pass needed. Offered rather than performed:
/// a full reload of a table that fell behind can be hours of work, and nobody asked for it just
/// because a pass failed.
/// </summary>
public sealed class ResyncTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(string Replication, string Mapping)> SetUpAsync()
    {
        var replicationName = $"resync-{Guid.NewGuid():N}";

        // A real connection, never opened. The resync has to know which engine spelled the stored
        // watermark key before it can clear the right row, and that comes from the connection's
        // driver type — see ResyncService.
        (await _client.PutAsJsonAsync("/api/connections/src", new ConnectionInput
        {
            Name = "src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "src", Database = "App" },
                Target = new EndpointRef { ConnectionName = "tgt", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", new TableMappingConfig
            {
                Name = "orders",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
            }, JsonOptions)).EnsureSuccessStatusCode();

        return (replicationName, "orders");
    }

    /// <summary>Records a failed run the way a runner would, so the endpoint is exercised against the
    /// state it actually reacts to rather than a hand-built object.</summary>
    private Guid RecordFailedRun(string replication, string mapping, string? failureKind)
    {
        // Enqueued and then completed, which is the only way a TaskRuns row comes into existence —
        // so this exercises the endpoint against state the runner could actually have produced.
        var runId = factory.Services.GetRequiredService<WorkQueueStore>()
            .Enqueue(replication, RunKind.Primary, mapping);
        factory.Services.GetRequiredService<TaskRunStore>()
            .CompleteRun(runId, RunStatus.Failed, 0, 0, "position gone", failureKind);
        return runId;
    }

    [Fact]
    public async Task APositionExpiredRun_CanBeResynced()
    {
        var (replication, mapping) = await SetUpAsync();
        var runId = RecordFailedRun(replication, mapping, RunFailureKinds.PositionExpired);

        var response = await _client.PostAsync($"/api/runs/{runId}/resync", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>
    /// Clearing the watermark is the half an operator constructing this by hand would forget, and
    /// without it the next incremental pass fails exactly as before — so it is the half worth pinning.
    /// </summary>
    [Fact]
    public async Task Resync_ClearsTheStoredPositionSoTheNextPassCanStart()
    {
        var (replication, mapping) = await SetUpAsync();
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var key = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" },
            MsSqlDialect.Instance);
        watermarks.SetWatermark(replication, mapping, key, "41");

        var runId = RecordFailedRun(replication, mapping, RunFailureKinds.PositionExpired);
        (await _client.PostAsync($"/api/runs/{runId}/resync", null)).EnsureSuccessStatusCode();

        Assert.Null(watermarks.GetWatermark(replication, mapping, key));
    }

    /// <summary>
    /// Any other failure is not this button's business. Offering it everywhere would make a full
    /// reload the general-purpose retry, which is exactly what "offered, not performed" is avoiding.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryFailure_IsNotResyncable()
    {
        var (replication, mapping) = await SetUpAsync();
        var runId = RecordFailedRun(replication, mapping, failureKind: null);

        var response = await _client.PostAsync($"/api/runs/{runId}/resync", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("did not fail because its source position expired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownRun_IsNotFound() =>
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.PostAsync($"/api/runs/{Guid.NewGuid()}/resync", null)).StatusCode);

    /// <summary>The failure kind reaches the client, because the Resync affordance is keyed off it.</summary>
    [Fact]
    public async Task TheFailureKind_IsReportedInRunHistory()
    {
        var (replication, mapping) = await SetUpAsync();
        RecordFailedRun(replication, mapping, RunFailureKinds.PositionExpired);

        var history = await _client.GetFromJsonAsync<List<TaskRunRecord>>(
            $"/api/replications/{replication}/runs", JsonOptions);

        Assert.Equal(RunFailureKinds.PositionExpired, Assert.Single(history!).FailureKind);
    }
}
