using System.Net;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The web assets — the SPA and the docs (phase 160) — reaching a browser that has **not signed in**, on a host with
/// authentication **on**. That is the only kind of reader who can open the sign-in screen, so the SPA and everything it
/// loads have to be served before authorization asks who is calling; the fallback policy ("anything unmarked is
/// closed", admin-only) would otherwise answer 401 to <c>/</c> itself and a default deployment could not show its own
/// sign-in page. Every other test in this project runs with authentication off and could not see this.
/// <para>
/// The docs the build copies into <c>wwwroot/docs/</c> (phase 160) reaching a browser through the host as it
/// stands — no docs-specific serving code exists, so this pins the two things the viewer depends on:
/// <c>/docs/&lt;page&gt;.md</c> is the file, served as Markdown, and <c>/docs</c> and <c>/docs/&lt;page&gt;</c> (no
/// extension) are the SPA's own routes rather than files. (The plan assumed <c>.md</c> needed a content-type
/// mapping; .NET 10's default table already has it, which a probe showed before any code was written for it.)
/// </para>
/// </summary>
public sealed class EmbeddedDocsServingTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("dbdatasync-webroot-").FullName;
    private readonly WebRootFactory _factory;

    public EmbeddedDocsServingTests()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "docs"));
        File.WriteAllText(Path.Combine(_webRoot, "docs", "install.md"), "# Install\n\n| a | b |\n| - | - |\n");
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html><body>the app</body></html>");
        _factory = new WebRootFactory(_webRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/invite")]
    [InlineData("/replications/sales/mappings/orders")]
    public async Task TheApp_IsServedToSomeoneWhoHasNotSignedIn(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("the app", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheApi_IsStillClosedToThem()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/about")).StatusCode);
    }

    /// <summary>An unmatched API route must never answer with the app's HTML, signed in or not.</summary>
    [Fact]
    public async Task AnUnmatchedApiRoute_IsNeverThePage_ForSomeoneWhoHasNotSignedIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/there-is-no-such-endpoint");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
        Assert.DoesNotContain("the app", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMarkdownFile_IsServedAsMarkdown_NotA404()
    {
        var response = await _factory.CreateClient().GetAsync("/docs/install.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("# Install", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The route the viewer lives at has no <c>.md</c> and no file behind it — the SPA answers it.</summary>
    [Theory]
    [InlineData("/docs")]
    [InlineData("/docs/install")]
    public async Task TheViewersOwnRoutes_AreTheApp_NotTheFiles(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("the app", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A path with a file extension is never the SPA's route — <c>MapFallback</c> skips file-shaped paths — so
    /// a page that is not there is never answered with the app's HTML for the viewer to render as Markdown. For someone
    /// who has not signed in it is 401 rather than 404, because no endpoint claims the path and the closed-by-default
    /// fallback policy is what answers it.</summary>
    [Fact]
    public async Task AMissingDoc_IsRefused_NotTheAppsHtml()
    {
        var response = await _factory.CreateClient().GetAsync("/docs/nothing-here.md");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
        Assert.DoesNotContain("the app", await response.Content.ReadAsStringAsync());
    }

    private sealed class WebRootFactory(string webRoot) : AuthenticatedApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(webRoot);
        }
    }
}
