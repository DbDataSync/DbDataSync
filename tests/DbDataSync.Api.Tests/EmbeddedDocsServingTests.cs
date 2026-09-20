using System.Net;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The docs the build copies into <c>wwwroot/docs/</c> (phase 160) reaching a browser through the host as it
/// stands — no docs-specific serving code exists, so this pins the two things the viewer depends on:
/// <c>/docs/&lt;page&gt;.md</c> is the file, served as Markdown, and <c>/docs</c> and <c>/docs/&lt;page&gt;</c> (no
/// extension) are the SPA's own routes rather than files. (The plan assumed <c>.md</c> needed a content-type
/// mapping; .NET 10's default table already has it, which a probe showed before any code was written for it.)
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
    /// a page that is not there is a plain 404, and the viewer never receives the app's HTML to render as Markdown.</summary>
    [Fact]
    public async Task AMissingDoc_IsA404_NotTheAppsHtml()
    {
        var response = await _factory.CreateClient().GetAsync("/docs/nothing-here.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("the app", await response.Content.ReadAsStringAsync());
    }

    private sealed class WebRootFactory(string webRoot) : TestApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(webRoot);
        }
    }
}
