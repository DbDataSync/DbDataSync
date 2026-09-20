using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 159's <see cref="UpdateService"/>, driven directly: a fake host (what this process is and how it was
/// started), a fake worker probe, a fake restart, and a fake network standing in for nuget.org and GitHub.
/// Nothing here touches a real service manager, a real installation or the internet.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string Running = "2026.9.16.1005";
    private const string Snapshot = "2026.9.19.1432-snapshot.g65615e7";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-update-svc-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- the fake world ---------------------------------------------------------------------------------

    internal static UpdateHostFacts Facts(
        string? version = Running, InstallKind kind = InstallKind.ToolPath, bool windows = false, bool linux = true, bool unit = true) =>
        new(version, new InstallLocation(kind, kind is InstallKind.Container or InstallKind.NotAToolInstall ? null : "/opt/dbdatasync"), windows, linux, unit);

    internal sealed class FakeProbe : IUpdateWorkProbe
    {
        public Queue<int> Script { get; } = new();
        public int Constant { get; set; }
        public int Calls { get; private set; }
        public bool Throws { get; set; }

        public int RunningWorkCount()
        {
            Calls++;
            if (Throws)
                throw new InvalidOperationException("the probe broke");
            return Script.Count > 0 ? Script.Dequeue() : Constant;
        }
    }

    internal sealed class FakeRestart : IUpdateRestart
    {
        public int Calls { get; private set; }

        public void StopForUpdate() => Calls++;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static readonly byte[] Package = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251)).ToArray();

    private static string GitHubJson() =>
        $$"""
        [{"tag_name":"snapshot-{{Snapshot}}","draft":false,"prerelease":true,
          "assets":[
           {"name":"DbDataSync.{{Snapshot}}.nupkg","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{Snapshot}}/DbDataSync.{{Snapshot}}.nupkg"},
           {"name":"DbDataSync.{{Snapshot}}.nupkg.sha512","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{Snapshot}}/DbDataSync.{{Snapshot}}.nupkg.sha512"}]}]
        """;

    internal static FakeNetwork Network(
        string? nuget = null, string? github = null, byte[]? served = null, HttpStatusCode? nugetStatus = null,
        List<string>? requested = null) =>
        new(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requested?.Add(url);
            if (url == ReleaseSources.NuGetIndexUrl)
                return nugetStatus is { } status
                    ? new HttpResponseMessage(status)
                    : Json(nuget ?? """{"versions":["2026.9.11.532","2026.9.16.1005","2026.9.18.1918","2026.9.12.721-beta"]}""");
            if (url.StartsWith(ReleaseSources.GitHubReleasesUrl, StringComparison.Ordinal))
                return Json(github ?? GitHubJson());
            if (url.EndsWith(".sha512", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Convert.ToBase64String(SHA512.HashData(Package))) };
            if (url.EndsWith(".nupkg", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served ?? Package) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private ApiOptions Options(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["DbDataSync:RepoRoot"] = _root,
            ["DbDataSync:SelfUpdateEnabled"] = "true",
            ["DbDataSync:SelfUpdateChannels"] = "stable,beta,snapshot",
            ["DbDataSync:SelfUpdateDrainTimeoutSeconds"] = "5",
        };
        foreach (var (key, value) in extra ?? [])
            values[key] = value;

        return ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private (UpdateService Service, FakeProbe Probe, FakeRestart Restart, UpdateDrainState Drain) Build(
        UpdateHostFacts? facts = null, Dictionary<string, string?>? options = null, FakeNetwork? network = null)
    {
        var probe = new FakeProbe();
        var restart = new FakeRestart();
        var drain = new UpdateDrainState();
        var service = new UpdateService(
            Options(options), new FakeFactory(network ?? Network()), facts ?? Facts(), drain, probe, restart,
            new FakeLifetime(), NullLogger<UpdateService>.Instance)
        {
            DrainPollInterval = TimeSpan.FromMilliseconds(10),
        };
        return (service, probe, restart, drain);
    }

    private UpdateStateStore Store() => new(new UpdateWorkspace(_root));

    // --- the settings ---------------------------------------------------------------------------------------

    [Fact]
    public void EverythingIsOffOrNarrowByDefault()
    {
        var options = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DbDataSync:RepoRoot"] = _root }).Build());

        Assert.False(options.SelfUpdateEnabled);
        Assert.Equal([ReleaseChannel.Stable], options.SelfUpdateChannels);
        Assert.Equal(TimeSpan.FromSeconds(120), options.SelfUpdateDrainTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SelfUpdateConfirmAfter);
    }

    [Theory]
    [InlineData("stable,beta", new[] { ReleaseChannel.Stable, ReleaseChannel.Beta })]
    [InlineData("Snapshot ; BETA", new[] { ReleaseChannel.Snapshot, ReleaseChannel.Beta })]
    [InlineData("stable,stable", new[] { ReleaseChannel.Stable })]
    // A typo must never widen what an update may install: the failure mode is the narrowest setting.
    [InlineData("nightly", new[] { ReleaseChannel.Stable })]
    [InlineData("", new[] { ReleaseChannel.Stable })]
    [InlineData("1,2", new[] { ReleaseChannel.Stable })]
    [InlineData("stable,nightly,snapshot", new[] { ReleaseChannel.Stable, ReleaseChannel.Snapshot })]
    public void Channels_AreReadLeniently_AndNeverWidenOnATypo(string configured, ReleaseChannel[] expected)
    {
        Assert.Equal(expected, ApiOptions.ReadChannels(configured));
    }

    // --- what is possible here ----------------------------------------------------------------------------

    [Fact]
    public void TurnedOff_ByDefault_SaysHowToTurnItOn()
    {
        var (service, _, _, _) = Build(options: new() { ["DbDataSync:SelfUpdateEnabled"] = "false" });

        var capability = service.Capability();

        Assert.False(capability.Enabled);
        Assert.False(capability.CanApply);
        Assert.Contains("DbDataSync:SelfUpdateEnabled", capability.Reason);
    }

    [Theory]
    [MemberData(nameof(Incapable))]
    public void EachThingThatWouldMakeUpdatingUnsafe_IsNamedInASentenceAnAdminCanActOn(UpdateHostFacts facts, string expected)
    {
        var (service, _, _, _) = Build(facts);

        var capability = service.Capability();

        Assert.True(capability.Enabled);
        Assert.False(capability.CanApply);
        Assert.Contains(expected, capability.Reason);
    }

    public static IEnumerable<object[]> Incapable() =>
    [
        [Facts(windows: true, linux: false), "not available on Windows yet"],
        [Facts(windows: false, linux: false), "only available on Linux"],
        [Facts(kind: InstallKind.Container), "pulling a newer image tag"],
        [Facts(kind: InstallKind.NotAToolInstall), "not installed as a dotnet tool"],
        [Facts(unit: false), "sudo dbdatasync service install"],
    ];

    [Fact]
    public void WhenEverythingIsInPlace_ItCanApply()
    {
        var (service, _, _, _) = Build();

        var capability = service.Capability();

        Assert.True(capability.Enabled);
        Assert.True(capability.CanApply);
        Assert.Null(capability.Reason);
    }

    // --- status ----------------------------------------------------------------------------------------------

    [Fact]
    public void Status_BeforeAnyUpdate_IsIdle()
    {
        var (service, _, _, _) = Build();

        var status = service.Status();

        Assert.Equal("idle", status.Phase);
        Assert.Equal(Running, status.RunningVersion);
        Assert.Equal("ToolPath", status.InstallKind);
        Assert.Equal(["stable", "beta", "snapshot"], status.Channels);
        Assert.True(status.CanApply);
        Assert.False(status.Pending);
        Assert.False(status.OnTrial);
        Assert.Empty(status.History);
    }

    [Fact]
    public void Status_ReflectsTheStateFiles()
    {
        var (service, _, _, _) = Build();
        var store = Store();
        store.Record(UpdatePhase.Failed, "it broke", "1.0", "2.0", "dan");
        store.Record(UpdatePhase.Restarting, "waiting", "2.0", "3.0", "dan");
        store.WritePending(new PendingUpdate("3.0", DateTimeOffset.UtcNow, "dan"));

        var status = service.Status();

        Assert.Equal("restarting", status.Phase);
        Assert.Equal("waiting", status.Message);
        Assert.Equal("3.0", status.ToVersion);
        Assert.Equal("dan", status.RequestedBy);
        Assert.True(status.Pending);
        Assert.True(status.OnTrial);
        var entry = Assert.Single(status.History);
        Assert.Equal("failed", entry.Phase);
        Assert.Equal("it broke", entry.Message);
    }

    // --- listing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Releases_AreListedNewestFirst_MarkingWhatIsRunningAndWhatIsNewer()
    {
        var (service, _, _, _) = Build();
        var warnings = new List<string>();

        var releases = await service.ListReleasesAsync(ReleaseChannel.Stable, 10, warnings, CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal(["2026.9.18.1918", "2026.9.16.1005", "2026.9.11.532"], releases.Select(r => r.Version));
        Assert.Equal(new[] { true, false, false }, releases.Select(r => r.Newer));
        Assert.Equal(new[] { false, true, false }, releases.Select(r => r.Installed));
        Assert.All(releases, r => Assert.Equal("stable", r.Channel));
    }

    [Fact]
    public async Task Releases_WithNoChannel_ListsEveryEnabledOne()
    {
        var (service, _, _, _) = Build();

        var releases = await service.ListReleasesAsync(null, 10, [], CancellationToken.None);

        Assert.Equal(["stable", "beta", "snapshot"], releases.Select(r => r.Channel).Distinct());
    }

    [Fact]
    public async Task Releases_OneChannelFailing_IsAWarning_ButAllFailingThrows()
    {
        var (partial, _, _, _) = Build(network: Network(nugetStatus: HttpStatusCode.BadGateway));
        var warnings = new List<string>();

        var releases = await partial.ListReleasesAsync(null, 10, warnings, CancellationToken.None);

        Assert.Contains(warnings, w => w.StartsWith("stable:", StringComparison.Ordinal));
        Assert.Contains(releases, r => r.Channel == "snapshot");

        var (allFail, _, _, _) = Build(
            options: new() { ["DbDataSync:SelfUpdateChannels"] = "stable" }, network: Network(nugetStatus: HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<ReleaseSourceException>(() => allFail.ListReleasesAsync(null, 10, [], CancellationToken.None));
    }

    // --- refusing an update -----------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_IsRefused_AndWritesNothing()
    {
        var (service, _, restart, drain) = Build(options: new() { ["DbDataSync:SelfUpdateEnabled"] = "false" });

        var result = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.Disabled, result.Outcome);
        Assert.Null(Store().ReadPending());
        Assert.False(drain.IsDraining);
        Assert.Equal(0, restart.Calls);
    }

    [Fact]
    public async Task AnInstallationThatCannotApply_IsRefusedWithItsReason()
    {
        var (service, _, _, _) = Build(Facts(unit: false));

        var result = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.CannotApply, result.Outcome);
        Assert.Contains("service install", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("../../etc/passwd")]
    [InlineData("2026.9.19.1432-alpha.1")]
    public async Task ANonVersion_IsRefused_BeforeAnythingIsLookedUp(string version)
    {
        var (service, _, _, _) = Build();

        var result = await service.RequestAsync(version, "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.InvalidVersion, result.Outcome);
    }

    [Fact]
    public async Task AChannelThatIsNotEnabled_IsRefused()
    {
        var (service, _, _, _) = Build(options: new() { ["DbDataSync:SelfUpdateChannels"] = "stable" });

        var result = await service.RequestAsync("2026.9.12.721-beta", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.ChannelNotEnabled, result.Outcome);
        Assert.Contains("SelfUpdateChannels", result.Message);
    }

    [Fact]
    public async Task AVersionThatIsNotInThePinnedSources_IsNotFound_WhateverTheClientSaid()
    {
        var (service, _, _, _) = Build();

        var result = await service.RequestAsync("2026.9.17.1", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.NotFound, result.Outcome);
        Assert.Null(Store().ReadPending());
    }

    [Fact]
    public async Task SourcesThatCannotBeReached_AreReportedAsSuch()
    {
        var (service, _, _, _) = Build(network: Network(nugetStatus: HttpStatusCode.ServiceUnavailable));

        var result = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.SourceUnavailable, result.Outcome);
        Assert.Contains("nuget.org answered 503", result.Message);
    }

    [Fact]
    public async Task TheVersionAlreadyRunning_IsRefused()
    {
        var (service, _, _, _) = Build();

        var result = await service.RequestAsync(Running, "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.AlreadyInstalled, result.Outcome);
    }

    // --- accepting one ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAcceptedUpdate_IsWrittenDown_ThenWindsDown_ThenAsksTheHostToStopWith75()
    {
        var (service, _, restart, drain) = Build();

        var result = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.Accepted, result.Outcome);
        var pending = Store().ReadPending()!;
        Assert.Equal("2026.9.18.1918", pending.TargetVersion);
        Assert.Equal("dan", pending.RequestedBy);

        // A version and who asked — nothing that could steer what root installs.
        var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Store().Workspace.PendingPath))
            .RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["requestedBy", "requestedUtc", "targetVersion"], written);

        await service.RestartTask;

        Assert.Equal(75, service.RequestedExitCode);
        Assert.Equal(1, restart.Calls);
        Assert.True(drain.IsDraining, "work stays refused until the process is gone");
        Assert.Equal(UpdatePhase.Applying, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task TheWindDown_WaitsForRunningWork_BeforeRestarting()
    {
        var (service, probe, restart, _) = Build();
        probe.Script.Enqueue(3);
        probe.Script.Enqueue(2);
        probe.Script.Enqueue(1);
        probe.Constant = 0;

        await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);
        Assert.Equal(UpdatePhase.Draining, Store().ReadState().Current!.Phase);
        await service.RestartTask;

        Assert.True(probe.Calls >= 4, "the probe was consulted until the work was gone");
        Assert.Equal(1, restart.Calls);
    }

    [Fact]
    public async Task WorkThatNeverFinishes_DoesNotHoldTheUpdateForever()
    {
        var (service, probe, restart, _) = Build(options: new() { ["DbDataSync:SelfUpdateDrainTimeoutSeconds"] = "0" });
        probe.Constant = 4;

        await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);
        await service.RestartTask;

        Assert.Equal(1, restart.Calls);
        Assert.Equal(75, service.RequestedExitCode);
    }

    [Fact]
    public async Task AnAcceptedSnapshot_IsJustAVersion_TheServiceDownloadsAndStagesNothing()
    {
        // The service is the unprivileged side of a trust boundary: anything it staged could be planted by a
        // compromised service, so the privileged step downloads the package itself from the pinned source.
        var requested = new List<string>();
        var (service, _, _, _) = Build(network: Network(requested: requested));

        var result = await service.RequestAsync(Snapshot, "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.Accepted, result.Outcome);
        Assert.Equal(Snapshot, Store().ReadPending()!.TargetVersion);
        Assert.DoesNotContain(requested, url => url.EndsWith(".nupkg", StringComparison.Ordinal) || url.EndsWith(".sha512", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_root, "updates", "staged")));
        await service.RestartTask;
    }

    [Fact]
    public async Task OnlyOneUpdateAtATime()
    {
        var (service, probe, _, _) = Build();
        probe.Constant = 1;

        var first = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);
        var second = await service.RequestAsync("2026.9.11.532", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.Accepted, first.Outcome);
        Assert.Equal(UpdateRequestOutcome.InProgress, second.Outcome);
        probe.Constant = 0;
        await service.RestartTask;
    }

    [Fact]
    public async Task AnUpdateOnTrial_BlocksAnotherUntilItIsConfirmedOrRolledBack()
    {
        var (service, _, _, _) = Build();
        Store().Record(UpdatePhase.Restarting, "Waiting.", Running, "2026.9.18.1918", "dan");

        var result = await service.RequestAsync("2026.9.11.532", "dan", CancellationToken.None);

        Assert.Equal(UpdateRequestOutcome.InProgress, result.Outcome);
    }

    [Fact]
    public async Task AWindDownThatBreaks_IsAbandoned_NotLeftDrainingForever()
    {
        var (service, probe, restart, drain) = Build();
        probe.Throws = true;

        await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);
        await service.RestartTask;

        Assert.Equal(0, restart.Calls);
        Assert.Null(service.RequestedExitCode);
        Assert.False(drain.IsDraining);
        Assert.Null(Store().ReadPending());
        var state = Store().ReadState().Current!;
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Contains("the probe broke", state.Message);

        probe.Throws = false;
        var again = await service.RequestAsync("2026.9.18.1918", "dan", CancellationToken.None);
        Assert.Equal(UpdateRequestOutcome.Accepted, again.Outcome);
        await service.RestartTask;
    }

    // --- starting up ------------------------------------------------------------------------------------------

    [Fact]
    public void ARequestTheUnitNeverApplied_IsReportedAndCleared_AtTheNextStart()
    {
        var (service, _, _, _) = Build();
        Store().WritePending(new PendingUpdate("2026.9.18.1918", DateTimeOffset.UtcNow, "dan"));

        service.ReconcileOnStartup();

        Assert.Null(Store().ReadPending());
        var state = Store().ReadState().Current!;
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Contains("not applied", state.Message);
        Assert.Contains("service install --self-update", state.Message);
    }

    [Fact]
    public void APhaseNoProcessIsCarryingAnyMore_IsMarkedInterrupted()
    {
        var (service, _, _, _) = Build();
        Store().Record(UpdatePhase.Draining, "Waiting.", Running, "2026.9.18.1918", "dan");

        service.ReconcileOnStartup();

        var state = Store().ReadState().Current!;
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Contains("interrupted", state.Message);
    }

    [Fact]
    public void AnUpdateOnTrial_IsLeftAloneAtStartup_ItsConfirmationIsTheConfirmationServicesJob()
    {
        var (service, _, _, _) = Build();
        Store().Record(UpdatePhase.Restarting, "Waiting.", Running, "2026.9.18.1918", "dan");

        service.ReconcileOnStartup();

        Assert.Equal(UpdatePhase.Restarting, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public void AFinishedOutcome_IsLeftAloneAtStartup()
    {
        var (service, _, _, _) = Build();
        Store().Record(UpdatePhase.Succeeded, "Updated.", Running, "2026.9.18.1918", "dan");

        service.ReconcileOnStartup();

        Assert.Equal(UpdatePhase.Succeeded, Store().ReadState().Current!.Phase);
    }
}
