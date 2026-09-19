using System.Net;
using System.Security.Cryptography;

namespace DbDataSync.Updates.Tests;

public class SnapshotStagerTests
{
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7");
    private const string BaseUrl = "https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-2026.9.19.1432-snapshot.g65615e7/";

    private static readonly ReleaseInfo Release = new(
        Version, ReleaseChannel.Snapshot,
        new Uri(BaseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg"),
        new Uri(BaseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg.sha512"));

    private static byte[] Package(int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static string Checksum(byte[] bytes) => Convert.ToBase64String(SHA512.HashData(bytes));

    /// <summary>Serves <paramref name="package"/> and a sidecar that describes <paramref name="sidecarFor"/>
    /// (the same bytes unless a test wants them to disagree).</summary>
    private static FakeHttpHandler Serving(byte[] package, byte[]? sidecarFor = null) => new(request =>
        request.RequestUri!.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal)
            ? Http.Ok(Checksum(sidecarFor ?? package) + "\n")
            : Http.Bytes(package));

    private static SnapshotStager Stager(FakeHttpHandler handler) => new(handler.Client());

    [Fact]
    public async Task Stage_DownloadsVerifiesAndLeavesTheFolderReadyToUseAsASource()
    {
        using var temp = new TempDirectory();
        var package = Package();

        var staged = await Stager(Serving(package)).StageAsync(Release, temp.Path);

        Assert.False(staged.Reused);
        Assert.Equal(Path.Combine(temp.Path, "2026.9.19.1432-snapshot.g65615e7"), staged.Directory);
        Assert.Equal(Path.Combine(staged.Directory, "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg"), staged.NupkgPath);
        Assert.Equal(package.Length, staged.SizeBytes);
        Assert.Equal(package, await File.ReadAllBytesAsync(staged.NupkgPath));
        Assert.Equal(Checksum(package), (await File.ReadAllTextAsync(staged.NupkgPath + ".sha512")).Trim());
        Assert.False(File.Exists(staged.NupkgPath + ".partial"));
    }

    [Fact]
    public async Task Stage_AChecksumMismatch_DiscardsWhatWasDownloaded_AndFails()
    {
        using var temp = new TempDirectory();
        var handler = Serving(Package(), sidecarFor: Package(size: 1000));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Stager(handler).StageAsync(Release, temp.Path));

        Assert.Contains("SHA-512", ex.Message);
        Assert.Contains("discarded", ex.Message);
        var directory = Path.Combine(temp.Path, Version.Text);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Stage_ATruncatedDownload_IsCaughtByTheSameCheck()
    {
        using var temp = new TempDirectory();
        var full = Package();
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal)
                ? Http.Ok(Checksum(full))
                : Http.Bytes(full[..(full.Length / 2)]));

        await Assert.ThrowsAsync<ReleaseSourceException>(() => Stager(handler).StageAsync(Release, temp.Path));

        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, Version.Text)));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public async Task Stage_ASidecarThatIsNotASha512_IsRefusedBeforeAnythingIsDownloaded(string sidecar)
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal) ? Http.Ok(sidecar) : Http.Bytes(Package()));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Stager(handler).StageAsync(Release, temp.Path));

        Assert.Contains("did not contain a SHA-512", ex.Message);
        Assert.DoesNotContain(handler.Sent, s => !s.Uri.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage_ReusesACopyAlreadyStaged_WithoutDownloadingAgain()
    {
        using var temp = new TempDirectory();
        var package = Package();
        await Stager(Serving(package)).StageAsync(Release, temp.Path);

        var second = Serving(package);
        var staged = await Stager(second).StageAsync(Release, temp.Path);

        Assert.True(staged.Reused);
        Assert.Empty(second.Sent);
    }

    [Fact]
    public async Task Stage_DoesNotTrustACopyThatNoLongerMatchesItsChecksum()
    {
        using var temp = new TempDirectory();
        var package = Package();
        var first = await Stager(Serving(package)).StageAsync(Release, temp.Path);
        await File.WriteAllBytesAsync(first.NupkgPath, package[..1000]);

        var staged = await Stager(Serving(package)).StageAsync(Release, temp.Path);

        Assert.False(staged.Reused);
        Assert.Equal(package, await File.ReadAllBytesAsync(staged.NupkgPath));
    }

    [Fact]
    public async Task Stage_IgnoresAPartialFileLeftByAnInterruptedRun()
    {
        using var temp = new TempDirectory();
        var package = Package();
        var directory = Path.Combine(temp.Path, Version.Text);
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg.partial");
        await File.WriteAllBytesAsync(partial, [1, 2, 3]);

        var staged = await Stager(Serving(package)).StageAsync(Release, temp.Path);

        Assert.Equal(package, await File.ReadAllBytesAsync(staged.NupkgPath));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task Stage_ADownloadThatDiesPartWay_LeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var package = Package();
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal)
                ? Http.Ok(Checksum(package))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new DyingContent(package.AsMemory(0, 1000).ToArray()) });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Stager(handler).StageAsync(Release, temp.Path));

        Assert.Contains("interrupted", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, Version.Text)));
    }

    [Fact]
    public async Task Stage_ANon200_IsAnActionableError_AndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith(".sha512", StringComparison.Ordinal)
                ? Http.Ok(Checksum(Package()))
                : Http.Status(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Stager(handler).StageAsync(Release, temp.Path));

        Assert.Contains("GitHub answered 404", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, Version.Text)));
    }

    [Fact]
    public async Task Stage_RefusesAReleaseThatIsNotASnapshot()
    {
        using var temp = new TempDirectory();
        var stable = new ReleaseInfo(ReleaseVersion.Parse("2026.9.18.1918"), ReleaseChannel.Stable);

        await Assert.ThrowsAsync<ArgumentException>(() => Stager(Serving(Package())).StageAsync(stable, temp.Path));
    }

    [Fact]
    public async Task Stage_RefusesAUrlThatIsNotAReleaseAssetOfThisRepository()
    {
        using var temp = new TempDirectory();
        var foreign = Release with { PackageUrl = new Uri("https://example.com/DbDataSync.nupkg") };
        var handler = Serving(Package());

        await Assert.ThrowsAsync<ArgumentException>(() => Stager(handler).StageAsync(foreign, temp.Path));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Stage_SendsAUserAgent_AndNeverAToken()
    {
        using var temp = new TempDirectory();
        var handler = Serving(Package());

        await Stager(handler).StageAsync(Release, temp.Path);

        Assert.All(handler.Sent, s =>
        {
            Assert.Equal("DbDataSync-update", s.UserAgent);
            Assert.Null(s.Authorization);
        });
    }

    /// <summary>A response body that produces some bytes and then fails, the way a dropped connection does.</summary>
    private sealed class DyingContent(byte[] first) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(first);
            throw new IOException("The connection was reset.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
