using System.Net;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The SPA fallback, and the exclusion that keeps it from swallowing the API.
/// <para>
/// A fallback that returns <c>index.html</c> for an unmatched <c>/api</c> route answers with a 200 and
/// a page of HTML, and the client parses it as JSON and reports something incomprehensible. That
/// failure looks like a client bug from every angle except this one, which is why it is asserted here
/// rather than trusted to the order routes happen to be registered in.
/// </para>
/// </summary>
public sealed class ApiFallbackTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task AnUnmatchedApiRoute_IsA404AndNotThePage()
    {
        var response = await _client.GetAsync("/api/there-is-no-such-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hub's own path is the same case: a client that gets HTML back from a negotiate
    /// request fails somewhere far away from the cause.</summary>
    [Fact]
    public async Task AnUnmatchedHubRoute_IsA404AndNotThePage()
    {
        var response = await _client.GetAsync("/hubs/not-a-hub");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A deep link is one of the SPA's routes, not one of this server's. Running out of the repo there
    /// are no published web assets, so this asserts the honest message rather than the page — the page
    /// itself is covered by the Playwright suite, which runs against a real build.
    /// </summary>
    [Fact]
    public async Task ADeepLink_IsAnsweredByTheAppRatherThanTheRouter()
    {
        var response = await _client.GetAsync("/replications/sales/mappings/orders");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode is HttpStatusCode.OK || body.Contains("No web assets are published"),
            $"a deep link should reach the app, or say why it cannot; got {(int)response.StatusCode}: {body}");
    }

    /// <summary>Still reachable, which is the thing the fallback must not have broken.</summary>
    [Fact]
    public async Task TheApiItself_IsUnaffected() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/health")).StatusCode);
}
