using System.Net;
using Xunit;

namespace DataSync.Api.Tests;

public sealed class RunsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Trigger_WhenReplicationDoesNotExist_Returns404()
    {
        var response = await _client.PostAsync($"/api/replications/does-not-exist-{Guid.NewGuid():N}/runs", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WhenRunDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/runs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenNoActiveRun_Returns404()
    {
        var response = await _client.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logs_WhenRunDoesNotExist_ReturnsEmptyList()
    {
        var response = await _client.GetAsync($"/api/runs/{Guid.NewGuid()}/logs");
        response.EnsureSuccessStatusCode();
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }
}
