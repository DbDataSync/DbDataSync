using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The pause endpoint, and the eligibility answer the status endpoint now carries — phase 64.
/// </summary>
public sealed class PauseEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> CreateReplicationAsync(bool enabled = true)
    {
        var name = $"pause-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Enabled = enabled,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
        return name;
    }

    private Task<ReplicationStatus?> GetStatusAsync(string name) =>
        _client.GetFromJsonAsync<ReplicationStatus>($"/api/replications/{name}/status", JsonOptions);

    private async Task<ReplicationStatus?> SetPausedAsync(string name, bool paused, string? note)
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/replications/{name}/paused", new { paused, note }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReplicationStatus>(JsonOptions);
    }

    [Fact]
    public async Task AFreshReplication_IsEligibleAndUnheld()
    {
        var status = await GetStatusAsync(await CreateReplicationAsync());

        Assert.True(status!.ShouldRun);
        Assert.True(status.Enabled);
        Assert.False(status.Paused);
        Assert.Null(status.PauseNote);
    }

    [Fact]
    public async Task Pausing_MakesItIneligible_AndCarriesTheNote()
    {
        var name = await CreateReplicationAsync();

        var status = await SetPausedAsync(name, paused: true, note: "source is being reindexed");

        Assert.True(status!.Paused);
        Assert.False(status.ShouldRun);
        Assert.True(status.Enabled); // orthogonal — pausing never touches config's intent
        Assert.Equal("source is being reindexed", status.PauseNote);
    }

    [Fact]
    public async Task Resuming_RestoresEligibility()
    {
        var name = await CreateReplicationAsync();
        await SetPausedAsync(name, paused: true, note: "held");

        var status = await SetPausedAsync(name, paused: false, note: null);

        Assert.False(status!.Paused);
        Assert.True(status.ShouldRun);
        Assert.Null(status.PauseNote);
    }

    /// <summary>
    /// Both gates are independent, and either one closed is enough to stop it. A disabled replication
    /// that is also paused has to report both, because resuming it would still not start anything and
    /// an operator needs to see why.
    /// </summary>
    [Fact]
    public async Task DisabledAndPaused_ReportsBothGates()
    {
        var name = await CreateReplicationAsync(enabled: false);

        var status = await SetPausedAsync(name, paused: true, note: null);

        Assert.False(status!.Enabled);
        Assert.True(status.Paused);
        Assert.False(status.ShouldRun);

        var afterResume = await SetPausedAsync(name, paused: false, note: null);
        Assert.False(afterResume!.ShouldRun); // still disabled
    }

    /// <summary>
    /// The whole reason Paused lives in state. If this ever commits, the feature has become a slower
    /// spelling of Enabled.
    /// </summary>
    [Fact]
    public async Task Pausing_NeverCommitsToTheConfigRepo()
    {
        var name = await CreateReplicationAsync();
        var before = await _client.GetFromJsonAsync<List<CommitInfo>>(
            $"/api/replications/{name}/history", JsonOptions);

        await SetPausedAsync(name, paused: true, note: "held");
        await SetPausedAsync(name, paused: false, note: null);

        var after = await _client.GetFromJsonAsync<List<CommitInfo>>(
            $"/api/replications/{name}/history", JsonOptions);

        Assert.Equal(before!.Count, after!.Count);
    }

    [Fact]
    public async Task PausingAReplicationThatDoesNotExist_IsANotFound()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/replications/no-such-replication/paused", new { paused = true, note = (string?)null }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Notes are config, so unlike the pause note they do commit — and have to survive the
    /// round trip through YAML to be worth having.</summary>
    [Fact]
    public async Task ReplicationNotes_RoundTripThroughSave()
    {
        var name = await CreateReplicationAsync();
        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);
        task!.Notes = "## Owner\n\nThe warehouse team. Do not reload during month-end close.";

        (await _client.PutAsJsonAsync($"/api/replications/{name}", task, JsonOptions)).EnsureSuccessStatusCode();

        var reloaded = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);
        Assert.Equal(task.Notes, reloaded!.Notes);
    }
}
