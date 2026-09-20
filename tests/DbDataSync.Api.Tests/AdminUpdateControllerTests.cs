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
/// The Updates screen's endpoints (phase 159) over real HTTP, through the real authorization and the real
/// drain middleware. What decides an update is <see cref="UpdateServiceTests"/>' subject; this is who may ask,
/// what the wire looks like, and what a request in flight does to every other request.
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

    private static StringContent Body(string version) =>
        new(JsonSerializer.Serialize(new { version }), System.Text.Encoding.UTF8, "application/json");

    // --- who may ask ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/admin/update/status")]
    [InlineData("GET", "/api/admin/update/releases")]
    [InlineData("POST", "/api/admin/update/apply")]
    public async Task AViewer_IsRefusedByEveryEndpoint(string method, string path)
    {
        var client = await Factory().SignedInAsAsync(UserRole.Viewer);
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = Body("2026.9.18.1918");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await Factory().CreateClient().GetAsync("/api/admin/update/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- closed by default ------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTurnedOff_StatusSaysSo_AndTheOtherEndpointsAreForbidden()
    {
        var client = await Factory(f => f.Enabled = false).SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("canApply").GetBoolean());
        Assert.Contains("SelfUpdateEnabled", status.GetProperty("cannotApplyReason").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/update/releases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/update/apply", Body("2026.9.18.1918"))).StatusCode);
    }

    // --- status and releases ----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_DescribesThisInstallation()
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("canApply").GetBoolean());
        Assert.Equal("2026.9.16.1005", status.GetProperty("runningVersion").GetString());
        Assert.Equal("ToolPath", status.GetProperty("installKind").GetString());
        Assert.Equal("idle", status.GetProperty("phase").GetString());
        Assert.Equal("/var/lib/dbdatasync-update/update.log", status.GetProperty("logPath").GetString());
        Assert.Equal(["stable", "beta", "snapshot"], status.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Status_SaysWhyThisInstallationCannotApply()
    {
        var client = await Factory(f => f.HostFacts = UpdateServiceTests.Facts(unit: false)).SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("canApply").GetBoolean());
        Assert.Contains("service install", status.GetProperty("cannotApplyReason").GetString());
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

    // --- asking for an update ---------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_IsAcceptedAndBeginsWindingDown_ThenRefusesAnother()
    {
        var factory = Factory();
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        factory.Probe.Constant = 1;

        var first = await client.PostAsync("/api/admin/update/apply", Body("2026.9.18.1918"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/update/status", Web);
        Assert.Equal("draining", status.GetProperty("phase").GetString());
        Assert.True(status.GetProperty("pending").GetBoolean());
        Assert.Equal("2026.9.18.1918", status.GetProperty("toVersion").GetString());

        var second = await client.PostAsync("/api/admin/update/apply", Body("2026.9.11.532"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        factory.Probe.Constant = 0;
        await factory.Updates.RestartTask;
        Assert.Equal(1, factory.Restart.Calls);
        Assert.Equal(75, factory.Updates.RequestedExitCode);
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("not-a-version", HttpStatusCode.BadRequest)]
    [InlineData("../../etc/passwd", HttpStatusCode.BadRequest)]
    [InlineData("2026.9.17.1", HttpStatusCode.NotFound)]
    [InlineData("2026.9.16.1005", HttpStatusCode.Conflict)]
    public async Task Apply_Refusals_MapToTheRightStatus(string version, HttpStatusCode expected)
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/update/apply", Body(version));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Apply_WithNoVersionAtAll_IsABadRequest()
    {
        var client = await Factory().SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/update/apply", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Apply_OnAnInstallationThatCannotApply_IsAConflictWithTheReason()
    {
        var client = await Factory(f => f.HostFacts = UpdateServiceTests.Facts(windows: true, linux: false)).SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/update/apply", Body("2026.9.18.1918"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not available on Windows yet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Apply_OfAChannelThatIsNotEnabled_IsForbidden()
    {
        var client = await Factory(f => f.Channels = "stable").SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/update/apply", Body("2026.9.12.721-beta"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- what an update in flight does to everything else -----------------------------------------------------

    [Fact]
    public async Task WhileAnUpdateDrains_AnythingThatWouldChangeSomething_Answers409_ButReadsAndTheUpdateScreenStillWork()
    {
        var factory = Factory();
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var put = () => new HttpRequestMessage(HttpMethod.Put, "/api/connections/drain-probe")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        var before = await client.SendAsync(put());
        Assert.NotEqual(HttpStatusCode.Conflict, before.StatusCode);

        factory.Probe.Constant = 1;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/admin/update/apply", Body("2026.9.18.1918"))).StatusCode);

        var during = await client.SendAsync(put());
        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        Assert.Contains("An update is being applied", await during.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/update/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);

        factory.Probe.Constant = 0;
        await factory.Updates.RestartTask;
    }

    [Fact]
    public async Task TheDrainStateIsWhatGatesRequests_NotTheUpdateEndpointItself()
    {
        var factory = Factory();
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var drain = factory.Services.GetRequiredService<UpdateDrainState>();

        drain.Begin();
        var during = await client.PutAsync("/api/connections/drain-probe", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        drain.End();
        var after = await client.PutAsync("/api/connections/drain-probe", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, after.StatusCode);
    }
}
