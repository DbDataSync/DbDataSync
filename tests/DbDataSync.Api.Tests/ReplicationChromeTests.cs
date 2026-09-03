using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The two endpoints phase 46's persistent chrome needs: what the worker is doing, and the one field
/// that commits on its own.
/// </summary>
public sealed class ReplicationChromeTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> CreateReplicationAsync(bool enabled = true)
    {
        var name = $"chrome-{Guid.NewGuid():N}";
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

    /// <summary>
    /// A worker drains its queue and exits, so a replication that is caught up has no process between
    /// cycles. Not running is the common state and not a fault, which is why it is a distinct answer
    /// rather than a 404.
    /// </summary>
    [Fact]
    public async Task AReplicationWithNoWorker_ReportsNotRunningRatherThanAnError()
    {
        var name = await CreateReplicationAsync();

        var status = await _client.GetFromJsonAsync<ReplicationStatus>(
            $"/api/replications/{name}/status", JsonOptions);

        Assert.False(status!.Running);
        Assert.Null(status.Pid);
        Assert.Null(status.MemoryBytes);
    }

    [Fact]
    public async Task AReplicationThatDoesNotExist_IsANotFound()
    {
        var response = await _client.GetAsync("/api/replications/no-such-replication/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_CommitsOnItsOwn()
    {
        var name = await CreateReplicationAsync(enabled: true);

        (await _client.PutAsJsonAsync($"/api/replications/{name}/enabled", new { enabled = false }, JsonOptions))
            .EnsureSuccessStatusCode();

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);
        Assert.False(task!.Enabled);
    }

    /// <summary>
    /// The reason this is its own endpoint. Toggling Enabled from a tab with a half-finished edit
    /// sitting in the Overview's draft must commit the toggle and nothing else — a control that says
    /// "Enabled" saving someone's unfinished pipeline change is a change nobody asked for.
    /// </summary>
    [Fact]
    public async Task Enabled_LeavesEveryOtherFieldExactlyAsItWasSaved()
    {
        var name = await CreateReplicationAsync();

        (await _client.PutAsJsonAsync($"/api/replications/{name}/enabled", new { enabled = false }, JsonOptions))
            .EnsureSuccessStatusCode();

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);

        Assert.Equal("MsSqlChangeTracking", task!.ChangeProcessing.Reader.Kind);
        Assert.Equal(3600, task.Scheduling.FrequencySeconds);
        Assert.Equal(ScheduleMode.Continuous, task.Scheduling.Mode);
    }

    [Fact]
    public async Task EnablingAgain_IsJustAsDirect()
    {
        var name = await CreateReplicationAsync(enabled: false);

        (await _client.PutAsJsonAsync($"/api/replications/{name}/enabled", new { enabled = true }, JsonOptions))
            .EnsureSuccessStatusCode();

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);
        Assert.True(task!.Enabled);
    }

    [Fact]
    public async Task SettingEnabledOnAReplicationThatDoesNotExist_IsANotFound()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/replications/no-such-replication/enabled", new { enabled = true }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
