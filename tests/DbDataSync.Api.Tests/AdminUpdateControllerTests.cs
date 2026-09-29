using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Services;
using DbDataSync.State;
using DbDataSync.Updates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The Updates screen's endpoints over real HTTP, through the real authorization. What the status says is
/// <see cref="UpdateServiceTests"/>' subject; this is who may ask and what the wire looks like. Since phase 196L there
/// is nothing to post: an update is applied from a shell, with the commands the status carries.
/// </summary>
public sealed class AdminUpdateControllerTests : IDisposable
{
    private readonly List<UpdateApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
    }

    private UpdateApiFactory Factory(Action<UpdateApiFactory>? configure = null)
    {
        var factory = new UpdateApiFactory();
        configure?.Invoke(factory);
        _factories.Add(factory);
        return factory;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // --- who may ask ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/admin/update/status")]
    [InlineData("GET", "/api/admin/update/releases")]
    public async Task AViewer_IsRefusedByEveryEndpoint(string method, string path)
    {
        var client = await Factory().SignedInAsAsync(UserRole.Viewer);
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    /// <summary>Phase 196L retired the console's apply: there is no endpoint to post an update to.</summary>
    [Fact]
    public async Task ThereIsNoApplyEndpoint()
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/update/apply",
            new StringContent("""{"version":"2026.9.18.1918"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await Factory().CreateClient().GetAsync("/api/admin/update/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- closed by default ------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTurnedOff_StatusSaysSo_AndStillGivesTheCommands_ButReleasesAreForbidden()
    {
        var client = await Factory(f => f.Enabled = false).SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("dbdatasync update --list", status.GetProperty("commands").GetProperty("list").GetString());

        var releases = await client.GetAsync("/api/admin/update/releases");
        Assert.Equal(HttpStatusCode.Forbidden, releases.StatusCode);
        Assert.Contains("Updates:Mode", await releases.Content.ReadAsStringAsync());
    }

    // --- status and releases ----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_DescribesThisInstallation_AndTheCommandsThatUpdateIt()
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("2026.9.16.1005", status.GetProperty("runningVersion").GetString());
        Assert.Equal("ToolPath", status.GetProperty("installKind").GetString());
        Assert.Equal("idle", status.GetProperty("phase").GetString());
        Assert.EndsWith("update.log", status.GetProperty("logPath").GetString());
        Assert.Equal(["stable", "beta", "snapshot"], status.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        var commands = status.GetProperty("commands");
        Assert.StartsWith("sudo dbdatasync update --to {version} --apply --repo ", commands.GetProperty("apply").GetString());
        Assert.True(commands.GetProperty("convertsFirst").GetBoolean());
        Assert.Equal(JsonValueKind.Null, commands.GetProperty("rollback").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("slots").ValueKind);
    }

    [Fact]
    public async Task Status_ForAContainer_HasNoCommands_AndSaysWhy()
    {
        var client = await Factory(f => f.HostFacts = UpdateServiceTests.Facts(kind: InstallKind.Container)).SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);

        Assert.Equal(JsonValueKind.Null, status.GetProperty("commands").ValueKind);
        Assert.Contains("image tag", status.GetProperty("commandsUnavailableReason").GetString());
    }

    [Fact]
    public async Task Releases_AreListedForAChannel_NewestFirst()
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/releases?channel=stable&limit=2", Web);

        var releases = body.GetProperty("releases").EnumerateArray().ToList();
        Assert.Equal(["2026.9.18.1918", "2026.9.16.1005"], releases.Select(r => r.GetProperty("version").GetString()));
        Assert.True(releases[0].GetProperty("newer").GetBoolean());
        Assert.True(releases[1].GetProperty("installed").GetBoolean());
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Releases_OfAChannelThatIsNotEnabled_AreForbidden_AndAnUnknownOneIsABadRequest()
    {
        var client = await Factory(f => f.Channels = "stable").SignedInAsAsync(UserRole.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/update/releases?channel=snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/update/releases?channel=nightly")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/update/releases?channel=1")).StatusCode);
    }

    [Fact]
    public async Task Releases_WhenNothingCanBeReached_Is502()
    {
        var client = await Factory(f =>
        {
            f.Channels = "stable";
            f.Network = UpdateServiceTests.Network(nugetStatus: HttpStatusCode.BadGateway);
        }).SignedInAsAsync(UserRole.Admin);

        var response = await client.GetAsync("/api/admin/update/releases");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("nuget.org answered 502", await response.Content.ReadAsStringAsync());
    }
}
