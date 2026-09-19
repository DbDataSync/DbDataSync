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

    private UpdateEnvironment Env(
        string? installed = "2026.9.16.1005", string baseDirectory = ToolPathBase, bool container = false,
        bool windows = false, bool inputRedirected = true, ServiceSituation? service = null, Action<string>? onLookup = null) =>
        new(installed, baseDirectory, "/home/dan/.dotnet/tools", container, windows, Path.Combine(_temp, "stage"),
            root =>
            {
                onLookup?.Invoke(root);
                return service ?? ServiceSituation.None;
            },
            inputRedirected, GitHubToken: null);

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
}
