using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>The instance's identity, over real HTTP and the real authorization (phase 160).</summary>
public sealed class AboutControllerTests : IDisposable
{
    private readonly UpdateApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(UserRole.Viewer)]
    [InlineData(UserRole.Admin)]
    public async Task EverySignedInRole_ReadsTheRunningVersion(UserRole role)
    {
        var client = await _factory.SignedInAsAsync(role);

        var about = await client.GetFromJsonAsync<JsonElement>("/api/about", Web);

        Assert.Equal(_factory.HostFacts.RunningVersion, about.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await _factory.CreateClient().GetAsync("/api/about");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
