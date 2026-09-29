using System.Net;
using System.Security.Cryptography;
using System.Text;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Updates;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The Updates screen's <see cref="UpdateService"/>, driven directly: a fake host (what this process is and how it
/// was started) and a fake network standing in for nuget.org and GitHub. Since phase 196L it changes nothing — it
/// lists, describes, and gives the commands that update the install from a shell.
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
        string? version = Running, InstallKind kind = InstallKind.ToolPath, bool windows = false, LauncherContext? launcher = null) =>
        new(version, new InstallLocation(kind, kind is InstallKind.Container or InstallKind.NotAToolInstall ? null : "/opt/dbdatasync"), windows, !windows, launcher);

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
          "html_url":"https://github.com/DbDataSync/DbDataSync/releases/tag/snapshot-{{Snapshot}}",
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
            ["DbDataSync:App:RepoRoot"] = _root,
            ["DbDataSync:Updates:Mode"] = "manual",
            ["DbDataSync:Updates:Channels"] = "stable,beta,snapshot",
        };
        foreach (var (key, value) in extra ?? [])
            values[key] = value;

        return ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private UpdateService Build(UpdateHostFacts? facts = null, Dictionary<string, string?>? options = null, FakeNetwork? network = null) =>
        new(Options(options), new FakeFactory(network ?? Network()), facts ?? Facts());

    private UpdateStateStore Store() => new(new UpdateWorkspace(_root));

    /// <summary>A slot install on disk, as the launcher would describe it: slot a running, b holding
    /// <paramref name="other"/> (or empty).</summary>
    private LauncherContext Slots(string? other)
    {
        var tool = Path.Combine(_root, "tool");
        Put(SlotPaths.SlotDirectory(tool, "a"), Running);
        if (other is not null)
            Put(SlotPaths.SlotDirectory(tool, "b"), other);
        new SlotLayout(tool).Flip("a");
        return new LauncherContext(tool, "a");

        static void Put(string slot, string version)
        {
            var any = Path.Combine(slot, ".store", "dbdatasync", version, "dbdatasync", version, "tools", "net10.0", "any");
            Directory.CreateDirectory(any);
            File.WriteAllText(Path.Combine(any, SlotPaths.PayloadAssemblyName), "payload");
        }
    }

    // --- the settings ---------------------------------------------------------------------------------------

    [Fact]
    public void EverythingIsOffOrNarrowByDefault()
    {
        var options = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DbDataSync:App:RepoRoot"] = _root }).Build());

        Assert.Equal(UpdatesMode.Disabled, options.SelfUpdateMode);
        Assert.Equal([ReleaseChannel.Stable], options.SelfUpdateChannels);
    }

    [Theory]
    [InlineData("stable,beta", new[] { ReleaseChannel.Stable, ReleaseChannel.Beta })]
    [InlineData("Snapshot ; BETA", new[] { ReleaseChannel.Snapshot, ReleaseChannel.Beta })]
    [InlineData("stable,stable", new[] { ReleaseChannel.Stable })]
    // A typo must never widen what the screen offers: the failure mode is the narrowest setting.
    [InlineData("nightly", new[] { ReleaseChannel.Stable })]
    [InlineData("", new[] { ReleaseChannel.Stable })]
    [InlineData("1,2", new[] { ReleaseChannel.Stable })]
    [InlineData("stable,nightly,snapshot", new[] { ReleaseChannel.Stable, ReleaseChannel.Snapshot })]
    public void Channels_AreReadLeniently_AndNeverWidenOnATypo(string configured, ReleaseChannel[] expected)
    {
        Assert.Equal(expected, ApiOptions.ReadChannels(configured));
    }

    [Fact]
    public void TurnedOff_SaysHowToTurnItOn()
    {
        var service = Build(options: new() { ["DbDataSync:Updates:Mode"] = "disabled" });

        Assert.False(service.Enabled);
        Assert.Contains("DbDataSync:Updates:Mode", service.DisabledReason);
    }

    // --- the commands ----------------------------------------------------------------------------------------

    [Fact]
    public void OnLinux_TheCommandsUseSudoToChangeAnything_AndNameTheDataDirectory()
    {
        var commands = Build().Status().Commands!;

        Assert.Equal("dbdatasync update --list", commands.List);
        Assert.Equal($"dbdatasync update --status --repo {_root}", commands.Status);
        Assert.Equal($"sudo dbdatasync update --to {{version}} --apply --repo {_root}", commands.Apply);
        Assert.Equal("{version}", commands.VersionPlaceholder);
        Assert.Contains("need root", commands.Where);
    }

    [Fact]
    public void OnWindows_TheCommandsSayToElevate()
    {
        var commands = Build(Facts(windows: true)).Status().Commands!;

        Assert.DoesNotContain("sudo", commands.Apply);
        Assert.Contains("Run as administrator", commands.Where);
    }

    [Fact]
    public void TurnedOff_StillGivesTheCommands_BecauseTheyNeedNothingFromTheConsole()
    {
        var status = Build(options: new() { ["DbDataSync:Updates:Mode"] = "disabled" }).Status();

        Assert.False(status.Enabled);
        Assert.NotNull(status.Commands);
    }

    [Theory]
    [InlineData(InstallKind.Container, "pulling a newer image tag")]
    [InlineData(InstallKind.NotAToolInstall, "not installed as a dotnet tool")]
    public void WhereThereIsNothingToUpdateInPlace_ThereAreNoCommands_AndItSaysWhy(InstallKind kind, string expected)
    {
        var status = Build(Facts(kind: kind)).Status();

        Assert.Null(status.Commands);
        Assert.Contains(expected, status.CommandsUnavailableReason);
    }

    [Fact]
    public void APreSlotMachineWideInstall_IsConvertedByTheFirstApply_AndHasNothingToRollBackTo()
    {
        var commands = Build().Status().Commands!;

        Assert.True(commands.ConvertsFirst);
        Assert.False(commands.PrintsOnly);
        Assert.Null(commands.Rollback);
    }

    [Fact]
    public void AGlobalTool_OnlyGetsTheCommandThatPrintsItsDotnetCommands()
    {
        var commands = Build(Facts(kind: InstallKind.Global)).Status().Commands!;

        Assert.True(commands.PrintsOnly);
        Assert.False(commands.ConvertsFirst);
        Assert.Equal("dbdatasync update --to {version}", commands.Apply);
    }

    // --- slots ---------------------------------------------------------------------------------------------------

    [Fact]
    public void UnderTheLauncher_StatusShowsBothSlots_AndOffersARollbackToTheOther()
    {
        var status = Build(Facts(launcher: Slots(other: "2026.9.11.532"))).Status();

        Assert.Equal("a", status.Slots!.Current);
        Assert.Equal(
            [("a", Running, true), ("b", "2026.9.11.532", false)],
            status.Slots.Slots.Select(s => (s.Name, s.Version, s.Current)));
        Assert.Empty(status.Slots.Checks);
        Assert.Equal($"sudo dbdatasync update --rollback --repo {_root}", status.Commands!.Rollback);
        Assert.False(status.Commands.ConvertsFirst);
    }

    [Fact]
    public void UnderTheLauncher_AnEmptyOtherSlot_HasNoRollback()
    {
        var status = Build(Facts(launcher: Slots(other: null))).Status();

        Assert.Null(status.Slots!.Slots[1].Version);
        Assert.Null(status.Commands!.Rollback);
    }

    [Fact]
    public void UnderTheLauncher_RunningTheOlderOfTwo_IsANote()
    {
        var status = Build(Facts(launcher: Slots(other: "2026.9.18.1918"))).Status();

        var check = Assert.Single(status.Slots!.Checks);
        Assert.Equal("note", check.Level);
        Assert.Contains("--to 2026.9.18.1918 --apply", check.Message);
    }

    // --- status ----------------------------------------------------------------------------------------------

    [Fact]
    public void Status_BeforeAnyUpdate_IsIdle()
    {
        var status = Build().Status();

        Assert.Equal("idle", status.Phase);
        Assert.Equal(Running, status.RunningVersion);
        Assert.Equal("ToolPath", status.InstallKind);
        Assert.Equal(["stable", "beta", "snapshot"], status.Channels);
        Assert.Null(status.Slots);
        Assert.Empty(status.History);
        Assert.Equal(Path.Combine(_root, "updates", "update.log"), status.LogPath);
    }

    /// <summary>The CLI records its progress in the same files, so an update run from a shell shows here.</summary>
    [Fact]
    public void Status_ReflectsWhatTheCliRecorded()
    {
        var store = Store();
        store.Record(UpdatePhase.Failed, "it broke", "1.0", "2.0", "dan");
        store.Record(UpdatePhase.Restarting, "Switched to slot b; starting 3.0.", "2.0", "3.0", "dan");

        var status = Build().Status();

        Assert.Equal("restarting", status.Phase);
        Assert.Equal("Switched to slot b; starting 3.0.", status.Message);
        Assert.Equal("3.0", status.ToVersion);
        Assert.Equal("dan", status.RequestedBy);
        var entry = Assert.Single(status.History);
        Assert.Equal("failed", entry.Phase);
        Assert.Equal("it broke", entry.Message);
    }

    // --- listing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Releases_AreListedNewestFirst_MarkingWhatIsRunningAndWhatIsNewer()
    {
        var service = Build();
        var warnings = new List<string>();

        var releases = await service.ListReleasesAsync(ReleaseChannel.Stable, 10, warnings, CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal(["2026.9.18.1918", "2026.9.16.1005", "2026.9.11.532"], releases.Select(r => r.Version));
        Assert.Equal(new[] { true, false, false }, releases.Select(r => r.Newer));
        Assert.Equal(new[] { false, true, false }, releases.Select(r => r.Installed));
        Assert.All(releases, r => Assert.Equal("stable", r.Channel));
    }

    /// <summary>A stable/beta release has no page of its own from nuget.org's flat-container index, so
    /// its Url is built from the nuget.org package-version URL scheme; a snapshot already carries its
    /// GitHub Release page (<c>html_url</c>) and that is used as-is.</summary>
    [Fact]
    public async Task Releases_ExposeAHumanReadablePage_NugetForStableAndBeta_GitHubForSnapshot()
    {
        var service = Build();

        var releases = await service.ListReleasesAsync(null, 10, [], CancellationToken.None);

        var stable = Assert.Single(releases, r => r.Channel == "stable" && r.Version == "2026.9.18.1918");
        Assert.Equal("https://www.nuget.org/packages/DbDataSync/2026.9.18.1918", stable.Url);
        var snapshot = Assert.Single(releases, r => r.Channel == "snapshot");
        Assert.Equal($"https://github.com/DbDataSync/DbDataSync/releases/tag/snapshot-{Snapshot}", snapshot.Url);
    }

    [Fact]
    public async Task Releases_WithNoChannel_ListsEveryEnabledOne()
    {
        var service = Build();

        var releases = await service.ListReleasesAsync(null, 10, [], CancellationToken.None);

        Assert.Equal(["stable", "beta", "snapshot"], releases.Select(r => r.Channel).Distinct());
    }

    [Fact]
    public async Task Releases_OneChannelFailing_IsAWarning_ButAllFailingThrows()
    {
        var partial = Build(network: Network(nugetStatus: HttpStatusCode.BadGateway));
        var warnings = new List<string>();

        var releases = await partial.ListReleasesAsync(null, 10, warnings, CancellationToken.None);

        Assert.Contains(warnings, w => w.StartsWith("stable:", StringComparison.Ordinal));
        Assert.Contains(releases, r => r.Channel == "snapshot");

        var allFail = Build(
            options: new() { ["DbDataSync:Updates:Channels"] = "stable" }, network: Network(nugetStatus: HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<ReleaseSourceException>(() => allFail.ListReleasesAsync(null, 10, [], CancellationToken.None));
    }
}
