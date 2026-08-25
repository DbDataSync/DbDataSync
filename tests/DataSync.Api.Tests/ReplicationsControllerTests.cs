using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using Xunit;

namespace DataSync.Api.Tests;

public sealed class ReplicationsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static ReplicationTaskConfig MakeTask(string name) => new()
    {
        Name = name,
        Enabled = true,
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
    };

    [Fact]
    public async Task Upsert_ThenGet_RoundTrips()
    {
        var name = $"repl-{Guid.NewGuid():N}";

        var putResponse = await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);
        putResponse.EnsureSuccessStatusCode();

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);

        Assert.NotNull(task);
        Assert.Equal(name, task!.Name);
        Assert.Equal(ScheduleMode.Continuous, task.Scheduling.Mode);
        Assert.Equal("MsSqlChangeTracking", task.ChangeProcessing.Reader.Kind);
    }

    [Fact]
    public async Task List_IncludesUpsertedReplication()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        var names = await _client.GetFromJsonAsync<List<string>>("/api/replications", JsonOptions);

        Assert.Contains(name, names!);
    }

    [Fact]
    public async Task Get_WhenNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/replications/does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        var deleteResponse = await _client.DeleteAsync($"/api/replications/{name}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/replications/{name}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
