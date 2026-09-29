using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataSync.Updates;
using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync update</c> end to end — real catalog, stager, planner and renderer — against a fake network
/// and a fake machine. Nothing here touches nuget.org or GitHub, and nothing changes an installation, which is
/// also what the command itself never does.
/// </summary>
public class UpdateCommandTests : IDisposable
{
    private const string ToolPathBase =
        "/opt/dbdatasync/.store/dbdatasync/2026.9.16.1005/dbdatasync/2026.9.16.1005/tools/net10.0/any/";

    private const string SnapshotA = "2026.9.19.1432-snapshot.g65615e7";
    private const string SnapshotB = "2026.9.19.1615-snapshot.gabcdef1";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dbdatasync-update-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _package = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();

    public UpdateCommandTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // --- the fake world -----------------------------------------------------------------------------------

    private static string Checksum(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

    private static string GitHubJson() => "[" + string.Join(",", new[] { SnapshotB, SnapshotA }.Select(v =>
        $$"""
        {"tag_name":"snapshot-{{v}}","draft":false,"prerelease":true,"html_url":"https://github.com/DbDataSync/DbDataSync/releases/tag/snapshot-{{v}}",
         "assets":[
          {"name":"DbDataSync.{{v}}.nupkg","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{v}}/DbDataSync.{{v}}.nupkg"},
          {"name":"DbDataSync.{{v}}.nupkg.sha512","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{v}}/DbDataSync.{{v}}.nupkg.sha512"}]}
        """)) + "]";

    private const string NuGetJson = """{"versions":["2026.9.11.532","2026.9.16.1005","2026.9.18.1918","2026.9.12.721-beta"]}""";

    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private FakeNetwork Network(
        HttpStatusCode? gitHubStatus = null, HttpStatusCode? nugetStatus = null, byte[]? served = null, byte[]? checksumOf = null) =>
        new(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReleaseSources.NuGetIndexUrl)
                return nugetStatus is { } n ? new HttpResponseMessage(n) : Json(NuGetJson);
            if (url.StartsWith(ReleaseSources.GitHubReleasesUrl, StringComparison.Ordinal))
                return gitHubStatus is { } g ? new HttpResponseMessage(g) : Json(GitHubJson());
            if (url.EndsWith(".sha512", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Checksum(checksumOf ?? _package)) };
            if (url.EndsWith(".nupkg", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served ?? _package) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private readonly List<string> _events = [];

    private UpdateEnvironment Env(
        string? installed = "2026.9.16.1005", string baseDirectory = ToolPathBase, bool container = false,
        bool windows = false, bool inputRedirected = true, ServiceSituation? service = null, Action<string>? onLookup = null,
        int stopExit = 0, int startExit = 0, bool healthy = true, Func<IReadOnlyList<string>, int>? dotnetExit = null,
        LauncherContext? launcher = null) =>
        new(installed, baseDirectory, "/home/dan/.dotnet/tools", container, windows, Path.Combine(_temp, "stage"),
            root =>
            {
                onLookup?.Invoke(root);
                return service ?? ServiceSituation.None;
            },
            inputRedirected, GitHubToken: null,
            new FakeServiceControl(_events, stopExit, startExit), new FakeHealth(_events, healthy),
            _ => new RecordingRunner(_events, dotnetExit), "dan", launcher, new FakeRebinder(_events));

    private sealed class FakeRebinder(List<string> events) : IServiceRebinder
    {
        public int Rebind(string dataRoot, string launcherPath, ServiceSituation service, TextWriter output)
        {
            events.Add($"rebind {service.Name} -> {launcherPath}");
            return 0;
        }
    }

    private sealed class FakeServiceControl(List<string> events, int stopExit, int startExit) : IServiceControl
    {
        public int Stop(ServiceSituation service)
        {
            events.Add("service stop");
            return stopExit;
        }

        public int Start(ServiceSituation service)
        {
            events.Add("service start");
            return startExit;
        }
    }

    private sealed class FakeHealth(List<string> events, bool healthy) : IHealthProbe
    {
        public Task<bool> WaitUntilHealthyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            events.Add($"health {baseUrl} {(int)timeout.TotalSeconds}s");
            return Task.FromResult(healthy);
        }
    }

    /// <summary>Records each <c>dotnet</c> run and, for a successful <c>tool install --tool-path</c>, lays the payload
    /// down where the real one would — what the flow checks before it switches.</summary>
    private sealed class RecordingRunner(List<string> events, Func<IReadOnlyList<string>, int>? exit) : IToolCommandRunner
    {
        public Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            events.Add("dotnet " + string.Join(' ', arguments));
            var code = exit?.Invoke(arguments) ?? 0;
            if (code == 0 && arguments is ["tool", "install", "--tool-path", var slot, ..])
                PutPayload(slot, arguments[arguments.ToList().IndexOf("--version") + 1]);
            return Task.FromResult(new ToolCommandResult(code, code == 0 ? "ok" : "it broke"));
        }
    }

    /// <summary>What <c>dotnet tool install --tool-path</c> leaves, as far as a slot is concerned; returns the payload
    /// directory (what AppContext.BaseDirectory is when that slot runs).</summary>
    private static string PutPayload(string slotDirectory, string version)
    {
        var any = Path.Combine(slotDirectory, ".store", "dbdatasync", version, "dbdatasync", version, "tools", "net10.0", "any");
        Directory.CreateDirectory(any);
        File.WriteAllText(Path.Combine(any, SlotPaths.PayloadAssemblyName), "payload");
        return any + Path.DirectorySeparatorChar;
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(
        string[] args, UpdateEnvironment env, FakeNetwork network, string input = "")
    {
        using var http = new HttpClient(network);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await UpdateCommand.RunAsync(args, env, http, new StringReader(input), output, error, CancellationToken.None);
        return (exit, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    // --- listing -----------------------------------------------------------------------------------------

    [Fact]
    public async Task List_PrintsEachChannelNewestFirst_MarkingWhatIsInstalledAndWhatIsNewer()
    {
        var (exit, output, error) = await RunAsync(["--list", "--channel", "stable", "--limit", "2"], Env(), Network());

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            "Installed: 2026.9.16.1005\n" +
            "\n" +
            "stable\n" +
            "  2026.9.18.1918  built 2026-09-18 19:18 UTC  newer\n" +
            "  2026.9.16.1005  built 2026-09-16 10:05 UTC  installed\n",
            output);
    }

    [Fact]
    public async Task List_AllChannels_ShowsStableBetaAndSnapshot()
    {
        var (exit, output, _) = await RunAsync(["--list"], Env(), Network());

        Assert.Equal(0, exit);
        Assert.Contains("\nstable\n", output);
        Assert.Contains("\nbeta\n", output);
        Assert.Contains("\nsnapshot\n", output);
        Assert.Contains(SnapshotA, output);
        Assert.Contains("2026.9.12.721-beta", output);
        Assert.DoesNotContain(") ", output);
    }

    [Fact]
    public async Task List_Json_IsMachineReadable()
    {
        var (exit, output, _) = await RunAsync(["--list", "--json", "--channel", "beta"], Env(), Network());

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("beta", entry.GetProperty("channel").GetString());
        Assert.Equal("2026.9.12.721-beta", entry.GetProperty("version").GetString());
        Assert.False(entry.GetProperty("installed").GetBoolean());
        Assert.False(entry.GetProperty("newer").GetBoolean());
    }

    [Fact]
    public async Task WithRedirectedInput_AndNoTarget_ItListsInsteadOfWaitingOnAPrompt()
    {
        var (exit, output, _) = await RunAsync([], Env(inputRedirected: true), Network());

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Install which one", output);
    }

    [Fact]
    public async Task ASourceThatFails_IsAWarning_WhileAnotherAnswers()
    {
        var (exit, output, error) = await RunAsync(["--list"], Env(), Network(gitHubStatus: HttpStatusCode.Forbidden));

        Assert.Equal(0, exit);
        Assert.Contains("snapshot: GitHub answered 403", error);
        Assert.Contains("\nstable\n", output);
        Assert.DoesNotContain("\nsnapshot\n", output);
    }

    [Fact]
    public async Task EverySourceFailing_IsAFailure()
    {
        var (exit, _, error) = await RunAsync(
            ["--list"], Env(), Network(gitHubStatus: HttpStatusCode.BadGateway, nugetStatus: HttpStatusCode.BadGateway));

        Assert.Equal(1, exit);
        Assert.Contains("stable: nuget.org answered 502", error);
        Assert.Contains("snapshot: GitHub answered 502", error);
    }

    [Theory]
    [InlineData("--channel", "nightly", "Unknown channel 'nightly'")]
    [InlineData("--channel", "1", "Unknown channel '1'")]
    [InlineData("--limit", "0", "--limit must be a whole number of at least 1")]
    [InlineData("--limit", "many", "--limit must be a whole number of at least 1")]
    [InlineData("--to", "not-a-version", "'not-a-version' is not a DbDataSync version")]
    public async Task BadOptions_AreRefusedBeforeAnythingIsFetched(string option, string value, string expected)
    {
        var network = Network();

        var (exit, _, error) = await RunAsync([option, value], Env(), network);

        Assert.Equal(1, exit);
        Assert.Contains(expected, error);
        Assert.Empty(network.Requested);
    }

    // --- choosing by version ----------------------------------------------------------------------------

    [Fact]
    public async Task To_AStableVersion_PrintsTheCommands_WithoutDownloadingAnything()
    {
        var network = Network();

        var (exit, output, _) = await RunAsync(["--to", "2026.9.18.1918"], Env(), network);

        Assert.Equal(0, exit);
        Assert.Contains("Selected    2026.9.18.1918  (stable)", output);
        Assert.Contains("dotnet tool update --tool-path /opt/dbdatasync DbDataSync --version 2026.9.18.1918", output);
        Assert.Equal([ReleaseSources.NuGetIndexUrl], network.Requested);
    }

    [Fact]
    public async Task To_ASnapshot_StagesItInTheStageDirectory_AndPointsTheCommandAtIt()
    {
        var env = Env();

        var (exit, output, error) = await RunAsync(["--to", SnapshotA], env, Network());

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        var directory = Path.Combine(_temp, "stage", SnapshotA);
        Assert.Equal(_package, await File.ReadAllBytesAsync(Path.Combine(directory, $"DbDataSync.{SnapshotA}.nupkg")));
        Assert.Contains("Downloaded and verified (0.2 MB).", output);
        Assert.Contains($"Staged      {directory}  (checksum verified)", output);
        // Quoted when the temp path has a space in it, so only the parts that don't depend on that are exact.
        Assert.Contains("--add-source", output);
        Assert.Contains(directory, output.Split("--add-source")[1]);
        Assert.Contains($"--version {SnapshotA}", output);
    }

    [Fact]
    public async Task To_ASnapshot_HonoursAStageDirectoryOption()
    {
        var elsewhere = Path.Combine(_temp, "elsewhere");

        var (exit, _, _) = await RunAsync(["--to", SnapshotA, "--stage-dir", elsewhere], Env(), Network());

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(elsewhere, SnapshotA, $"DbDataSync.{SnapshotA}.nupkg")));
    }

    [Fact]
    public async Task To_ASnapshot_WithARelativeStageDirectory_IsStagedAndInstalledFromAnAbsolutePath()
    {
        // `dotnet` is run from a working directory of its own, so a relative --add-source would mean something else.
        var (exit, output, _) = await RunAsync(["--to", SnapshotA, "--stage-dir", "relative-stage"], Env(), Network());

        Assert.Equal(0, exit);
        var absolute = Path.Combine(Path.GetFullPath("relative-stage"), SnapshotA);
        Assert.Contains(absolute, output);
        Assert.True(Directory.Exists(absolute));
        Directory.Delete(Path.GetFullPath("relative-stage"), recursive: true);
    }

    [Fact]
    public async Task To_ASnapshotAlreadyStaged_IsNotDownloadedAgain()
    {
        await RunAsync(["--to", SnapshotA], Env(), Network());
        var second = Network();

        var (exit, output, _) = await RunAsync(["--to", SnapshotA], Env(), second);

        Assert.Equal(0, exit);
        Assert.Contains("Already staged and verified", output);
        Assert.DoesNotContain(second.Requested, url => url.EndsWith(".nupkg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task To_ASnapshotWhoseDownloadFailsItsChecksum_PrintsNoPlan()
    {
        var wrong = _package.Take(10).ToArray();

        var (exit, output, error) = await RunAsync(["--to", SnapshotA], Env(), Network(checksumOf: wrong));

        Assert.Equal(1, exit);
        Assert.Contains("does not match its published SHA-512 checksum", error);
        Assert.DoesNotContain("dotnet tool", output);
    }

    [Fact]
    public async Task To_TheVersionAlreadyInstalled_SaysSo_AndDownloadsNothing()
    {
        var network = Network();

        var (exit, output, _) = await RunAsync(["--to", SnapshotA], Env(installed: SnapshotA + "+abc123"), network);

        Assert.Equal(0, exit);
        Assert.Contains($"{SnapshotA} is already installed", output);
        Assert.DoesNotContain(network.Requested, url => url.EndsWith(".nupkg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task To_TheInstalledVersion_IsAnsweredWithoutTheNetwork_EvenIfItHasSinceBeenPruned()
    {
        var network = Network();
        const string pruned = "2026.9.1.100-snapshot.g1111111";

        var (exit, output, _) = await RunAsync(["--to", pruned], Env(installed: pruned + "+abc"), network);

        Assert.Equal(0, exit);
        Assert.Contains($"{pruned} is already installed", output);
        Assert.Empty(network.Requested);
    }

    [Fact]
    public async Task To_ALowerVersion_PrintsAnUninstallAndAnInstall()
    {
        var (exit, output, _) = await RunAsync(["--to", "2026.9.11.532"], Env(), Network());

        Assert.Equal(0, exit);
        Assert.Contains("dotnet tool uninstall --tool-path /opt/dbdatasync DbDataSync", output);
        Assert.Contains("dotnet tool install --tool-path /opt/dbdatasync DbDataSync --version 2026.9.11.532", output);
    }

    [Fact]
    public async Task To_AVersionThatIsNotThere_IsAnError()
    {
        var (exit, output, error) = await RunAsync(["--to", "2026.9.17.1"], Env(), Network());

        Assert.Equal(1, exit);
        Assert.Contains("2026.9.17.1 is not a stable release that can be found", error);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task To_AVersionWhoseLabelIsNotAChannel_IsRefused()
    {
        var (exit, _, error) = await RunAsync(["--to", "2026.9.19.1432-alpha.1"], Env(), Network());

        Assert.Equal(1, exit);
        Assert.Contains("is not a stable, beta or snapshot version", error);
    }

    // --- the machine it runs on -------------------------------------------------------------------------

    [Fact]
    public async Task InAContainer_ItDeclinesToPrintInstallCommands_AndDoesNotDownloadASnapshot()
    {
        var network = Network();

        var (exit, output, _) = await RunAsync(["--to", SnapshotA], Env(container: true), network);

        Assert.Equal(0, exit);
        Assert.Contains("container image", output);
        Assert.DoesNotContain(network.Requested, url => url.EndsWith(".nupkg", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_temp, "stage")));
    }

    [Fact]
    public async Task FromADevelopmentBuild_ItStillStagesASnapshot_ButSaysThereIsNothingToUpdate()
    {
        var env = Env(baseDirectory: "/src/DbDataSync/src/DbDataSync.Cli/bin/Debug/net10.0/");

        var (exit, output, _) = await RunAsync(["--to", SnapshotA], env, Network());

        Assert.Equal(0, exit);
        Assert.Contains("was not installed as a dotnet tool", output);
        Assert.Contains("The staged snapshot is in", output);
        Assert.DoesNotContain("dotnet tool update", output);
    }

    [Fact]
    public async Task UnderASystemdService_ThePlanStopsAndStartsIt()
    {
        var env = Env(service: new ServiceSituation(ServiceManager.Systemd, "dbdatasync"));

        var (_, output, _) = await RunAsync(["--to", "2026.9.18.1918"], env, Network());

        Assert.Contains("systemctl stop dbdatasync", output);
        Assert.Contains("systemctl start dbdatasync", output);
    }

    [Fact]
    public async Task OnWindows_UnderAWindowsService_ThePlanUsesScExe()
    {
        var env = Env(
            baseDirectory: @"C:\Program Files\DbDataSync\.store\dbdatasync\2026.9.16.1005\dbdatasync\2026.9.16.1005\tools\net10.0\any\",
            windows: true, service: new ServiceSituation(ServiceManager.WindowsService, "DbDataSync"));

        var (_, output, _) = await RunAsync(["--to", "2026.9.18.1918"], env, Network());

        Assert.Contains("sc.exe stop DbDataSync", output);
        Assert.Contains("sc.exe start DbDataSync", output);
        Assert.Contains("--tool-path \"C:\\Program Files\\DbDataSync\" DbDataSync --version 2026.9.18.1918", output);
    }

    [Fact]
    public async Task TheServiceIsLookedUpAtTheResolvedRepoRoot()
    {
        string? looked = null;

        await RunAsync(["--to", "2026.9.18.1918", "--repo", "/srv/dbdatasync"], Env(onLookup: root => looked = root), Network());

        Assert.Equal("/srv/dbdatasync", looked);
    }

    // --- choosing interactively -------------------------------------------------------------------------

    [Fact]
    public async Task Interactive_NumbersTheReleases_AndPlansTheOneChosenByNumber()
    {
        // Listed as 1) 2026.9.18.1918  2) 2026.9.16.1005  3) 2026.9.11.532  4) the beta  5) SnapshotB  6) SnapshotA
        var (exit, output, _) = await RunAsync(["--channel", "all"], Env(inputRedirected: false), Network(), input: "1\n");

        Assert.Equal(0, exit);
        Assert.Contains("  1) 2026.9.18.1918", output);
        Assert.Contains("  6) " + SnapshotA, output);
        Assert.Contains("Install which one?", output);
        Assert.Contains("Selected    2026.9.18.1918  (stable)", output);
    }

    [Fact]
    public async Task Interactive_AcceptsAVersionTypedInPlaceOfANumber_AndStagesASnapshot()
    {
        var (exit, output, _) = await RunAsync([], Env(inputRedirected: false), Network(), input: SnapshotB + "\n");

        Assert.Equal(0, exit);
        Assert.Contains($"Selected    {SnapshotB}  (snapshot)", output);
        Assert.True(File.Exists(Path.Combine(_temp, "stage", SnapshotB, $"DbDataSync.{SnapshotB}.nupkg")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Interactive_AnEmptyAnswer_Cancels_WithoutError(string answer)
    {
        var network = Network();

        var (exit, output, error) = await RunAsync([], Env(inputRedirected: false), network, input: answer + "\n");

        Assert.Equal(0, exit);
        Assert.Contains("Cancelled.", output);
        Assert.Equal("", error);
        Assert.DoesNotContain(network.Requested, url => url.EndsWith(".nupkg", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("2026.1.1.1")]
    public async Task Interactive_AnAnswerThatIsNotListed_IsAnError(string answer)
    {
        var (exit, output, error) = await RunAsync([], Env(inputRedirected: false), Network(), input: answer + "\n");

        Assert.Equal(1, exit);
        Assert.Contains($"'{answer}' is not one of the releases listed", error);
        Assert.DoesNotContain("Selected", output);
    }

    [Fact]
    public async Task Interactive_EndOfInput_Cancels()
    {
        var (exit, output, _) = await RunAsync([], Env(inputRedirected: false), Network(), input: "");

        Assert.Equal(0, exit);
        Assert.Contains("Cancelled.", output);
    }

    // --- --apply: a slot install (phase 196L) ----------------------------------------------------------------

    private static readonly ServiceSituation Systemd = new(ServiceManager.Systemd, "dbdatasync");

    private string DataRoot => Path.Combine(_temp, "data");

    private string ToolRoot => Path.Combine(_temp, "tool");

    private string SlotA => SlotPaths.SlotDirectory(ToolRoot, "a");

    private string SlotB => SlotPaths.SlotDirectory(ToolRoot, "b");

    private string[] Apply(params string[] extra) => ["--to", "2026.9.18.1918", "--apply", "--repo", DataRoot, .. extra];

    private UpdateStateStore Store() => new(new UpdateWorkspace(DataRoot));

    private SlotLayout Layout => new(ToolRoot);

    /// <summary>A slot install running 2026.9.16.1005 from slot a, as the launcher would have started it.</summary>
    private UpdateEnvironment SlotEnv(
        ServiceSituation? service = null, bool healthy = true, int stopExit = 0, Func<IReadOnlyList<string>, int>? dotnetExit = null,
        bool inputRedirected = true, bool windows = false, string? otherSlot = null)
    {
        var payload = PutPayload(SlotA, "2026.9.16.1005");
        if (otherSlot is not null)
            PutPayload(SlotB, otherSlot);
        Layout.Flip("a");
        return Env(baseDirectory: payload, service: service, healthy: healthy, stopExit: stopExit, dotnetExit: dotnetExit,
            inputRedirected: inputRedirected, windows: windows, launcher: new LauncherContext(ToolRoot, "a"));
    }

    private string InstallIntoB(string version = "2026.9.18.1918") =>
        $"dotnet tool install --tool-path {SlotB} DbDataSync --version {version}";

    [Fact]
    public async Task Apply_UnderAService_InstallsIntoTheOtherSlot_ThenStopsSwitchesStartsAndChecks()
    {
        var (exit, output, error) = await RunAsync(Apply("--yes"), SlotEnv(service: Systemd), Network());

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal([InstallIntoB(), "service stop", "service start", "health http://localhost:5080 90s"], _events);
        Assert.Equal("b", Layout.Current);
        Assert.Equal("2026.9.16.1005", Layout.Slot("a").Version);
        Assert.Contains("Updated to 2026.9.18.1918. The service is answering.", output);
        Assert.Contains("`dbdatasync update --rollback` switches back", output);
        Assert.Equal(UpdatePhase.Succeeded, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task Apply_TheServiceNeverAnswers_SwitchesBack_WithNothingToReinstall()
    {
        var (exit, output, error) = await RunAsync(Apply("--yes", "--health-timeout", "5"), SlotEnv(service: Systemd, healthy: false), Network());

        Assert.Equal(1, exit);
        Assert.Contains("did not answer at http://localhost:5080 within 5 seconds", output);
        Assert.Contains("Switched back: 2026.9.16.1005 is running again", error);
        Assert.Equal(
            [InstallIntoB(), "service stop", "service start", "health http://localhost:5080 5s", "service stop", "service start"],
            _events);
        Assert.Equal("a", Layout.Current);
        Assert.Equal("2026.9.18.1918", Layout.Slot("b").Version);
        Assert.Equal(UpdatePhase.RolledBack, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task Apply_AServiceThatCannotBeStopped_SwitchesNothing_AndSaysToUseSudo()
    {
        var (exit, _, error) = await RunAsync(Apply("--yes"), SlotEnv(service: Systemd, stopExit: 1), Network());

        Assert.Equal(1, exit);
        Assert.Contains("Could not stop the service, so nothing was switched", error);
        Assert.Contains("sudo", error);
        Assert.Equal([InstallIntoB(), "service stop"], _events);
        Assert.Equal("a", Layout.Current);
    }

    [Fact]
    public async Task Apply_AFailedInstall_NeverStopsTheService()
    {
        var (exit, _, error) = await RunAsync(Apply("--yes"), SlotEnv(service: Systemd, dotnetExit: _ => 1), Network());

        Assert.Equal(1, exit);
        Assert.Contains("failed (exit 1)", error);
        Assert.Contains("Nothing was switched", error);
        Assert.Equal([InstallIntoB()], _events);
        Assert.Equal("a", Layout.Current);
        Assert.Equal(UpdatePhase.Failed, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task Apply_WithNoService_JustInstallsAndSwitches()
    {
        var (exit, output, _) = await RunAsync(Apply("--yes"), SlotEnv(), Network());

        Assert.Equal(0, exit);
        Assert.Equal([InstallIntoB()], _events);
        Assert.Equal("b", Layout.Current);
        Assert.Contains("restart a running `dbdatasync serve` yourself", output);
    }

    [Fact]
    public async Task Apply_TheTargetAlreadyInTheOtherSlot_DownloadsNothing_AndOnlySwitches()
    {
        var (exit, output, _) = await RunAsync(Apply("--yes"), SlotEnv(service: Systemd, otherSlot: "2026.9.18.1918"), Network());

        Assert.Equal(0, exit);
        Assert.Contains("Slot b already holds 2026.9.18.1918", output);
        Assert.Equal(["service stop", "service start", "health http://localhost:5080 90s"], _events);
        Assert.Equal("b", Layout.Current);
    }

    [Fact]
    public async Task Apply_OverAnOlderVersionInTheOtherSlot_ReplacesIt()
    {
        var (exit, _, _) = await RunAsync(Apply("--yes"), SlotEnv(otherSlot: "2026.9.11.532"), Network());

        Assert.Equal(0, exit);
        Assert.Equal(["2026.9.18.1918"], SlotPaths.InstalledVersions(SlotB));
    }

    [Fact]
    public async Task Apply_OnWindows_WorksTheSameWay()
    {
        var windowsService = new ServiceSituation(ServiceManager.WindowsService, "DbDataSync");

        var (exit, _, _) = await RunAsync(Apply("--yes"), SlotEnv(service: windowsService, windows: true), Network());

        Assert.Equal(0, exit);
        Assert.Equal([InstallIntoB(), "service stop", "service start", "health http://localhost:5080 90s"], _events);
        Assert.Equal("b", Layout.Current);
    }

    [Fact]
    public async Task Apply_ASnapshot_IsStagedThenInstalledIntoTheOtherSlotFromThere()
    {
        var (exit, _, _) = await RunAsync(["--to", SnapshotA, "--apply", "--yes", "--repo", DataRoot], SlotEnv(), Network());

        Assert.Equal(0, exit);
        var staged = Path.Combine(_temp, "stage", SnapshotA);
        Assert.Equal([$"dotnet tool install --tool-path {SlotB} DbDataSync --add-source {staged} --version {SnapshotA}"], _events);
    }

    [Fact]
    public async Task Apply_AsksFirst_AndAnythingButYesCancels()
    {
        var (exit, output, _) = await RunAsync(Apply(), SlotEnv(service: Systemd, inputRedirected: false), Network(), input: "n\n");

        Assert.Equal(0, exit);
        Assert.Contains("Apply this update now? The service will be stopped and started again. [y/N]", output);
        Assert.Contains("Cancelled.", output);
        Assert.Empty(_events);
        Assert.Equal("a", Layout.Current);
    }

    [Fact]
    public async Task Apply_AYesAtThePrompt_Proceeds()
    {
        var (exit, _, _) = await RunAsync(Apply(), SlotEnv(inputRedirected: false), Network(), input: "y\n");

        Assert.Equal(0, exit);
        Assert.Single(_events);
    }

    [Fact]
    public async Task Apply_WithNoTerminal_AndNoYes_RefusesRatherThanGuess()
    {
        var (exit, _, error) = await RunAsync(Apply(), SlotEnv(service: Systemd), Network());

        Assert.Equal(1, exit);
        Assert.Contains("Pass --yes", error);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Apply_TheVersionAlreadyRunning_IsNothingToDo()
    {
        var (exit, output, _) = await RunAsync(["--to", "2026.9.16.1005", "--apply", "--yes", "--repo", DataRoot], SlotEnv(), Network());

        Assert.Equal(0, exit);
        Assert.Contains("is already installed", output);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Apply_UsesTheConfiguredUrl_OrTheOneGiven()
    {
        await RunAsync(Apply("--yes", "--url", "https://example.test:5443"), SlotEnv(service: Systemd), Network());

        Assert.Contains("health https://example.test:5443 90s", _events);
    }

    [Fact]
    public async Task WithoutApply_UnderTheLauncher_ItPrintsTheApplyCommand_NotDotnetCommandsIntoTheRunningSlot()
    {
        var (exit, output, _) = await RunAsync(["--to", "2026.9.18.1918", "--repo", DataRoot], SlotEnv(), Network());

        Assert.Equal(0, exit);
        Assert.Contains($"sudo dbdatasync update --to 2026.9.18.1918 --apply --repo {DataRoot}", output);
        Assert.DoesNotContain("dotnet tool", output);
    }

    [Fact]
    public async Task WithoutApply_FromALegacyToolPathInstall_ItAlsoOffersTheApplyCommand()
    {
        var (exit, output, _) = await RunAsync(["--to", "2026.9.18.1918"], Env(), Network());

        Assert.Equal(0, exit);
        Assert.Contains("dotnet tool update --tool-path /opt/dbdatasync", output);
        Assert.Contains("converting this install to versioned slots first", output);
    }

    // --- --apply: converting a pre-196L install ----------------------------------------------------------------

    /// <summary>A pre-196L machine-wide install at <see cref="ToolRoot"/>: its shim, its store holding the running
    /// version's package, and that version's launcher beside its payload — what this process would be.</summary>
    private string LegacyPayload()
    {
        var version = "2026.9.16.1005";
        var kept = Path.Combine(ToolRoot, ".store", "dbdatasync", version, "dbdatasync", version);
        var payload = Path.Combine(kept, "tools", "net10.0", "any");
        var launcher = Path.Combine(payload, SlotPaths.LauncherDirectoryName, SlotPaths.PortableRuntimeIdentifier()!);
        Directory.CreateDirectory(launcher);
        File.WriteAllText(Path.Combine(kept, $"dbdatasync.{version}.nupkg"), "package");
        File.WriteAllText(Path.Combine(payload, SlotPaths.PayloadAssemblyName), "payload");
        File.WriteAllText(Path.Combine(launcher, "dbdatasync"), "launcher");
        File.WriteAllText(Path.Combine(ToolRoot, "dbdatasync"), "legacy shim");
        return payload + Path.DirectorySeparatorChar;
    }

    [Fact]
    public async Task Apply_OnALegacyInstall_ConvertsItFirst_Loudly_ThenUpdatesIntoSlotB()
    {
        var env = Env(baseDirectory: LegacyPayload(), service: Systemd);

        var (exit, output, error) = await RunAsync(Apply("--yes"), env, Network());

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Contains("predates versioned slots; converting it", output);
        Assert.Equal(5 + 1, _events.Count);
        Assert.StartsWith($"dotnet tool install --tool-path {SlotA} DbDataSync --add-source ", _events[0]);
        Assert.EndsWith("--version 2026.9.16.1005", _events[0]);
        Assert.Equal($"rebind dbdatasync -> {Path.Combine(ToolRoot, "dbdatasync")}", _events[1]);
        Assert.Equal([InstallIntoB(), "service stop", "service start", "health http://localhost:5080 90s"], _events[2..]);
        Assert.Equal("b", Layout.Current);
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(ToolRoot, "dbdatasync")));
    }

    [Fact]
    public async Task Apply_OnAGlobalInstall_RefusesAndPrintsTheCommandsInstead()
    {
        var env = Env(baseDirectory: "/home/dan/.dotnet/tools/.store/dbdatasync/2026.9.16.1005/dbdatasync/2026.9.16.1005/tools/net10.0/any/");

        var (exit, output, error) = await RunAsync(Apply("--yes"), env, Network());

        Assert.Equal(1, exit);
        Assert.Contains("A global tool", error);
        Assert.Contains("dotnet tool update --global DbDataSync --version 2026.9.18.1918", output);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Apply_FromADevelopmentBuild_ExplainsWhyThereIsNothingToUpdate()
    {
        var env = Env(baseDirectory: "/src/DbDataSync/src/DbDataSync.Cli/bin/Debug/net10.0/");

        var (exit, output, _) = await RunAsync(Apply("--yes"), env, Network());

        Assert.Equal(1, exit);
        Assert.Contains("was not installed as a dotnet tool", output);
        Assert.Empty(_events);
    }

    // --- --rollback --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_SwitchesToTheOtherSlot_TheSameWay_WithoutInstallingAnything()
    {
        var (exit, output, _) = await RunAsync(
            ["--rollback", "--yes", "--repo", DataRoot], SlotEnv(service: Systemd, otherSlot: "2026.9.11.532"), Network());

        Assert.Equal(0, exit);
        Assert.Contains("Switch to   2026.9.11.532  (slot b)", output);
        Assert.Contains("Rolled back to 2026.9.11.532.", output);
        Assert.Equal(["service stop", "service start", "health http://localhost:5080 90s"], _events);
        Assert.Equal("b", Layout.Current);
    }

    [Fact]
    public async Task Rollback_WithAnEmptyOtherSlot_HasNothingToGoBackTo()
    {
        var (exit, _, error) = await RunAsync(["--rollback", "--yes", "--repo", DataRoot], SlotEnv(service: Systemd), Network());

        Assert.Equal(1, exit);
        Assert.Contains("Slot b is empty", error);
        Assert.Empty(_events);
        Assert.Equal("a", Layout.Current);
    }

    [Fact]
    public async Task Rollback_OnAnInstallWithoutSlots_SaysSo()
    {
        var (exit, _, error) = await RunAsync(["--rollback", "--yes"], Env(), Network());

        Assert.Equal(1, exit);
        Assert.Contains("no versioned slots", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("soon")]
    public async Task BadHealthTimeout_IsRefused(string value)
    {
        var (exit, _, error) = await RunAsync(["--health-timeout", value], Env(), Network());

        Assert.Equal(1, exit);
        Assert.Contains("--health-timeout must be a whole number of seconds", error);
    }

    // --- --status ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_BeforeAnyUpdate_SaysSo_WithoutTouchingTheNetwork()
    {
        var network = Network();

        var (exit, output, _) = await RunAsync(["--status", "--repo", DataRoot], Env(), network);

        Assert.Equal(0, exit);
        Assert.Contains("Slots: none yet", output);
        Assert.Contains("no update has been attempted here", output);
        Assert.Empty(network.Requested);
    }

    [Fact]
    public async Task Status_ShowsBothSlots_WhichIsCurrent_AndTheHistory()
    {
        var env = SlotEnv(otherSlot: "2026.9.18.1918");
        Store().Record(UpdatePhase.RolledBack, "2026.9.18.1918 did not answer", "2026.9.16.1005", "2026.9.18.1918", "dan");

        var (_, output, _) = await RunAsync(["--status", "--repo", DataRoot], env, Network());

        Assert.Contains("  a  2026.9.16.1005  current", output);
        Assert.Contains("  b  2026.9.18.1918\n", output);
        Assert.Contains("note: Running 2026.9.16.1005, the older of the two installed", output);
        Assert.Contains("rolledback", output);
        Assert.Contains("history, newest first:", output);
    }
}
