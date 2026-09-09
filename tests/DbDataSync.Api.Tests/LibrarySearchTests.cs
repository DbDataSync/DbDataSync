using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Phase 119's NuGet search proxy — a stubbed <see cref="StubNuGetSearchHandler"/> stands in
/// for the real public index (`LibrarySearchApiFactory`), so these never touch the network.</summary>
public sealed class LibrarySearchTests : IDisposable
{
    private readonly LibrarySearchApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record ResultDto(
        string Id, string Description, string LatestVersion, List<string> Versions, long TotalDownloads, bool Verified);
    private sealed record ResponseDto(string Status, List<ResultDto>? Results);

    [Fact]
    public async Task ReshapesACapturedNuGetSearchPayload()
    {
        _factory.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "totalHits": 1,
                  "data": [
                    {
                      "id": "MySqlConnector",
                      "description": "A fully managed ADO.NET provider for MySQL",
                      "version": "2.4.0",
                      "versions": [
                        { "version": "2.3.0", "downloads": 100 },
                        { "version": "2.4.0", "downloads": 500 }
                      ],
                      "totalDownloads": 12345678,
                      "verified": true
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json"),
        };
        var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<ResponseDto>("/api/libraries/search?q=mysql");

        Assert.Equal("ok", response!.Status);
        var result = Assert.Single(response.Results!);
        Assert.Equal("MySqlConnector", result.Id);
        Assert.Equal("A fully managed ADO.NET provider for MySQL", result.Description);
        Assert.Equal("2.4.0", result.LatestVersion);
        Assert.Equal(["2.3.0", "2.4.0"], result.Versions);
        Assert.Equal(12345678, result.TotalDownloads);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task Disabled_Returns503WithADisabledStatus_AndNeverCallsTheHandler()
    {
        _factory.NuGetSearchEnabled = false;
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/libraries/search?q=mysql");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResponseDto>();
        Assert.Equal("disabled", body!.Status);
        Assert.Null(body.Results);
        Assert.False(_factory.Handler.WasCalled);
    }

    [Fact]
    public async Task AnUpstream500_ReturnsAnUnavailableStatus_NotA500()
    {
        _factory.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/libraries/search?q=mysql");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResponseDto>();
        Assert.Equal("unavailable", body!.Status);
        Assert.Null(body.Results);
    }

    [Fact]
    public async Task AnUpstreamTimeout_ReturnsAnUnavailableStatus_NotA500()
    {
        _factory.Handler.Respond = _ => throw new TaskCanceledException("simulated timeout");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/libraries/search?q=mysql");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ResponseDto>();
        Assert.Equal("unavailable", body!.Status);
    }
}

/// <summary>Against the real public NuGet index, no stub — the parent doc's own canary package. The
/// stubbed tests above are the real coverage (network flakiness shouldn't fail this suite); this only
/// confirms the reshaping still matches the live index's actual response shape.</summary>
[Trait("Category", "Integration")]
public sealed class LibrarySearchIntegrationTests : IDisposable
{
    private readonly TestApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record ResultDto(
        string Id, string Description, string LatestVersion, List<string> Versions, long TotalDownloads, bool Verified);
    private sealed record ResponseDto(string Status, List<ResultDto>? Results);

    [Fact]
    public async Task SearchingForMySqlConnector_FindsItOnTheRealIndex()
    {
        var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<ResponseDto>("/api/libraries/search?q=MySqlConnector");

        Assert.Equal("ok", response!.Status);
        var result = response.Results!.Single(r => r.Id == "MySqlConnector");
        Assert.NotEmpty(result.LatestVersion);
        Assert.NotEmpty(result.Versions);
        Assert.True(result.TotalDownloads > 0);
    }
}
