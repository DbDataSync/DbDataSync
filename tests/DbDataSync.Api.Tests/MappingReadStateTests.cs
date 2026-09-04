using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The endpoints an operator sets a mapping's read intent and hold through — beside
/// <c>refresh-metadata</c>, and only the storage half of the design: nothing here changes what a pass
/// actually does, because nothing consumes the intent yet. See phase 100.
/// </summary>
public sealed class MappingReadStateTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(string Replication, string Mapping)> SetUpAsync(ReadIntent? replicationDefault = null)
    {
        var replicationName = $"read-state-{Guid.NewGuid():N}";

        // A real connection, never opened — this endpoint only needs to know which engine spelled the
        // watermark key, the same reasoning ResyncService and ChangeSourceResolver already rely on.
        // Saved directly through ConfigRepository rather than PUT /api/connections/{name}: that
        // endpoint's Negotiate authentication handler throws under TestServer in this sandbox
        // (`NotSupportedException: ... requires a server that supports IConnectionItemsFeature like
        // Kestrel`), a pre-existing environment defect confirmed on a clean checkout and unrelated to
        // phase 100 — ResyncTests.cs hits the identical failure calling the same endpoint.
        factory.Services.GetRequiredService<ConfigRepository>().SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, Author);

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
            DefaultReadIntent = replicationDefault,
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

    private Task<HttpResponseMessage> GetReadStateAsync(string replication, string mapping) =>
        _client.GetAsync($"/api/replications/{replication}/table-mappings/{mapping}/read-state");

    private Task<HttpResponseMessage> SetReadStateAsync(
        string replication, string mapping, ReadIntent intent, ReadHold hold) =>
        _client.PostAsJsonAsync(
            $"/api/replications/{replication}/table-mappings/{mapping}/read-state",
            new { intent, hold }, JsonOptions);

    /// <summary>A mapping that has never run reports the application default, not an absence a client
    /// has to resolve itself — the guarantee that a full load always traces to somebody's choice
    /// depends on this being visible before a first pass, not only after one.</summary>
    [Fact]
    public async Task ANewMapping_ReportsTheApplicationDefault()
    {
        var (replication, mapping) = await SetUpAsync();

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.InitialLoad, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Null(state.Watermark);
    }

    [Fact]
    public async Task ANewMapping_ReportsTheReplicationsConfiguredDefault()
    {
        var (replication, mapping) = await SetUpAsync(replicationDefault: ReadIntent.ChangesFromLatest);

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.ChangesFromLatest, state!.Intent);
    }

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        var (replication, mapping) = await SetUpAsync();

        var setResponse = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);
        setResponse.EnsureSuccessStatusCode();

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
    }

    /// <summary>The one call that moves both at once: an operator recovering from a hold sets a fresh
    /// intent and clears the hold together, never in a request that could leave one half done.</summary>
    [Fact]
    public async Task SettingAnIntentAndClearingAHold_HappensInOneCall()
    {
        var (replication, mapping) = await SetUpAsync();
        (await SetReadStateAsync(replication, mapping, ReadIntent.InitialLoad, ReadHold.PositionExpired))
            .EnsureSuccessStatusCode();

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
    }

    /// <summary>An intent cannot move under a pass that is already acting on it.</summary>
    [Fact]
    public async Task SetReadState_WhileTheRunLockIsHeld_IsRefused()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.Primary, mapping, Guid.NewGuid()));

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // And nothing moved: a refused request must not have partially applied.
        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
        Assert.Equal(ReadIntent.InitialLoad, state!.Intent);
    }

    [Fact]
    public async Task SetReadState_OnceTheLockIsReleased_IsAllowedAgain()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.Primary, mapping, Guid.NewGuid()));
        runLocks.Release(replication, RunKind.Primary, mapping);

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>A Backfill lock is refused on the same footing as a Primary one — a reload in progress
    /// is still a pass acting on this mapping.</summary>
    [Fact]
    public async Task SetReadState_WhileABackfillLockIsHeld_IsRefused()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.Backfill, mapping, Guid.NewGuid()));

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetReadState_ForAMissingReplication_IsNotFound()
    {
        var response = await GetReadStateAsync("no-such-replication", "orders");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReadState_ForAMissingMapping_IsNotFound()
    {
        var (replication, _) = await SetUpAsync();
        var response = await GetReadStateAsync(replication, "no-such-mapping");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record MappingReadStateDto(ReadIntent Intent, ReadHold Hold, string? Watermark, DateTimeOffset? WatermarkTimeUtc);
}
