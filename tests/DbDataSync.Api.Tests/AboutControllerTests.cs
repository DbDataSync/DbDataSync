using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;
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
    public async Task NotesRichMarkdown_IsOffUnlessTheDeploymentTurnedItOn()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Viewer);

        var about = await client.GetFromJsonAsync<JsonElement>("/api/about", Web);

        Assert.False(about.GetProperty("notesRichMarkdown").GetBoolean());
    }

    /// <summary>A Viewer reads it — Notes are shown to Viewers, so the renderer choice cannot come from an Admin endpoint.</summary>
    [Fact]
    public async Task NotesRichMarkdown_IsReportedToAViewer_WhenTheDeploymentTurnedItOn()
    {
        using var factory = new NotesRichFactory();
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var about = await client.GetFromJsonAsync<JsonElement>("/api/about", Web);

        Assert.True(about.GetProperty("notesRichMarkdown").GetBoolean());
    }

    private sealed class NotesRichFactory : AuthenticatedApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["DbDataSync:Notes:MarkdownRenderer"] = "rich" }));
        }
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await _factory.CreateClient().GetAsync("/api/about");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
