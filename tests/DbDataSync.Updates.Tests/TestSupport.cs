using System.Net;
using System.Text;

namespace DbDataSync.Updates.Tests;

/// <summary>What a request looked like when it was sent — captured eagerly, because the code under test
/// disposes the message the moment it has its response.</summary>
internal sealed record SentRequest(Uri Uri, string? Authorization, string? UserAgent, string? Accept);

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SentRequest> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add(new SentRequest(
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString()));
        return Task.FromResult(respond(request));
    }

    public HttpClient Client() => new(this);
}

internal static class Http
{
    public static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdatasync-updates-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Records every <c>dotnet</c> invocation and answers each with a scripted exit code (0 unless a
/// predicate says otherwise).</summary>
internal sealed class FakeToolRunner(Func<IReadOnlyList<string>, int>? exitCode = null) : IToolCommandRunner
{
    public List<string> Commands { get; } = [];

    public Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Commands.Add(string.Join(' ', arguments));
        var code = exitCode?.Invoke(arguments) ?? 0;
        return Task.FromResult(new ToolCommandResult(code, code == 0 ? "ok" : "it broke"));
    }
}

/// <summary>
/// A data root (the service's), a separate privileged directory (root's), and a tool root with a version in its
/// store — plus a fake nuget.org and GitHub. The two directories are distinct on purpose: the point of the
/// privileged step is that it treats the first as hostile.
/// </summary>
internal sealed class ApplierWorld : IDisposable
{
    public const string Running = "2026.9.16.1005";
    public const string Snapshot = "2026.9.19.1432-snapshot.g65615e7";

    public static readonly byte[] SnapshotPackage = Enumerable.Range(0, 4000).Select(i => (byte)(i * 13 % 251)).ToArray();

    private readonly TempDirectory _data = new();
    private readonly TempDirectory _privileged = new();
    private readonly TempDirectory _tools = new();

    public ApplierWorld(
        Func<IReadOnlyList<string>, int>? dotnetExit = null, string? installed = Running,
        HttpStatusCode? nugetStatus = null, byte[]? snapshotServed = null,
        string nugetVersions = """["2026.9.11.532","2026.9.16.1005","2026.9.18.1918","2026.9.19.100","2026.9.12.721-beta"]""")
    {
        Store = new UpdateStateStore(new UpdateWorkspace(_data.Path, _privileged.Path));
        Runner = new FakeToolRunner(dotnetExit);
        Applier = new UpdateApplier(Store, Runner);

        Network = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReleaseSources.NuGetIndexUrl)
                return nugetStatus is { } status ? Http.Status(status) : Http.Ok($$"""{"versions":{{nugetVersions}}}""");
            if (url.StartsWith(ReleaseSources.GitHubReleasesUrl, StringComparison.Ordinal))
                return Http.Ok(GitHubJson());
            if (url.EndsWith(".sha512", StringComparison.Ordinal))
                return Http.Ok(Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(SnapshotPackage)));
            if (url.EndsWith(".nupkg", StringComparison.Ordinal))
                return Http.Bytes(snapshotServed ?? SnapshotPackage);
            return Http.Status(HttpStatusCode.NotFound);
        });

        if (installed is not null)
            PutInStore(installed, $"package of {installed}");
    }

    public UpdateStateStore Store { get; }
    public FakeToolRunner Runner { get; }
    public UpdateApplier Applier { get; }
    public FakeHttpHandler Network { get; }
    public string Tools => _tools.Path;
    public string Data => _data.Path;
    public string Privileged => _privileged.Path;
    public UpdateWorkspace Workspace => Store.Workspace;

    public void PutInStore(string version, string content)
    {
        var directory = Path.Combine(_tools.Path, ".store", "dbdatasync", version.ToLowerInvariant(), "dbdatasync", version.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"dbdatasync.{version.ToLowerInvariant()}.nupkg"), content);
    }

    /// <summary>What the privileged step knows for itself: this install, this version, the pinned sources.</summary>
    public PrivilegedContext Context(InstallLocation? location = null, string? running = Running)
    {
        var client = Network.Client();
        return new PrivilegedContext(location ?? new InstallLocation(InstallKind.ToolPath, Tools), running, new ReleaseCatalog(client), new SnapshotStager(client));
    }

    public Task<ApplyResult> ApplyPending(PrivilegedContext? context = null) =>
        Applier.ApplyPendingAsync(context ?? Context(), CancellationToken.None);

    /// <summary>A request as the service writes it.</summary>
    public void Request(string target, string? requestedBy = "dan") =>
        Store.WritePending(new PendingUpdate(target, DateTimeOffset.UtcNow, requestedBy));

    /// <summary>What an update the privileged step applied looks like, for a test that starts from "on trial".</summary>
    public UpdateRequest AppliedRequest(string target = "2026.9.19.100", string previous = Running, ReleaseChannel channel = ReleaseChannel.Stable) =>
        new(target, previous, channel, InstallKind.ToolPath, Tools, null, DateTimeOffset.UtcNow, "dan");

    /// <summary>A request as the CLI (or an inline caller) builds it — the whole thing, from a trusted operator.</summary>
    public UpdateRequest Full(string target, string? previous = Running, ReleaseChannel channel = ReleaseChannel.Stable, string? source = null) =>
        new(target, previous, channel, InstallKind.ToolPath, Tools, source, DateTimeOffset.UtcNow, "dan");

    private static string GitHubJson() =>
        $$"""
        [{"tag_name":"snapshot-{{Snapshot}}","draft":false,"prerelease":true,
          "assets":[
           {"name":"DbDataSync.{{Snapshot}}.nupkg","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{Snapshot}}/DbDataSync.{{Snapshot}}.nupkg"},
           {"name":"DbDataSync.{{Snapshot}}.nupkg.sha512","browser_download_url":"https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-{{Snapshot}}/DbDataSync.{{Snapshot}}.nupkg.sha512"}]}]
        """;

    public void Dispose()
    {
        _data.Dispose();
        _privileged.Dispose();
        _tools.Dispose();
    }
}
