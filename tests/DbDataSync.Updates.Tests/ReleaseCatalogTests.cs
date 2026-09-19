using System.Globalization;
using System.Net;

namespace DbDataSync.Updates.Tests;

/// <summary>
/// The nuget.org fixture is a real response, captured from the live index. The GitHub fixture is a real
/// response for this repository's releases (trimmed to the fields the catalog reads) with **synthetic**
/// snapshot entries added in front: no snapshot has been published yet, so those are shaped exactly like the
/// real release objects but invented — three valid ones, and seven each broken in one way the catalog must
/// skip (draft, not a prerelease, no checksum asset, an asset hosted somewhere else, the wrong asset name,
/// a scratch tag, a tag that is not a snapshot's).
/// </summary>
public class ReleaseCatalogTests
{
    private const string GitHubPage = ReleaseSources.GitHubReleasesUrl + "?per_page=100&page=";

    private static ReleaseCatalog Catalog(FakeHttpHandler handler, string? token = null) => new(handler.Client(), token);

    private static FakeHttpHandler Serving(string nuget, string github) => new(request =>
        request.RequestUri!.AbsoluteUri switch
        {
            ReleaseSources.NuGetIndexUrl => Http.Ok(nuget),
            var url when url.StartsWith(ReleaseSources.GitHubReleasesUrl, StringComparison.Ordinal) => Http.Ok(github),
            _ => Http.Status(HttpStatusCode.NotFound),
        });

    private static FakeHttpHandler Fixtures() => Serving(Fixture.Read("nuget-flat-container.json"), Fixture.Read("github-releases.json"));

    [Fact]
    public async Task Stable_ListsNuGetsStableVersions_NewestFirst()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 10);

        Assert.Equal(
            ["2026.9.18.1918", "2026.9.16.1005", "2026.9.16.506", "2026.9.12.547", "2026.9.11.532"],
            releases.Select(r => r.Version.Text));
        Assert.All(releases, r =>
        {
            Assert.Equal(ReleaseChannel.Stable, r.Channel);
            Assert.Null(r.PackageUrl);
            Assert.Null(r.ChecksumUrl);
        });
    }

    [Fact]
    public async Task Beta_ListsOnlyBetaVersions()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Beta, 10);

        Assert.Equal(
            ["2026.9.12.721-beta", "2026.9.11.450-beta", "2026.9.11.425-beta"],
            releases.Select(r => r.Version.Text));
    }

    [Fact]
    public async Task Limit_KeepsTheNewest()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 2);

        Assert.Equal(["2026.9.18.1918", "2026.9.16.1005"], releases.Select(r => r.Version.Text));
    }

    [Fact]
    public async Task Limit_BelowOne_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 0));
    }

    [Fact]
    public async Task NuGet_IgnoresVersionsThatAreNotOneOfTheProductsChannels()
    {
        var handler = Serving("""{"versions":["2026.9.18.1918","2026.09.19.1432-alpha.1789","not-a-version","2026.9.12.721-beta"]}""", "[]");

        Assert.Equal(["2026.9.18.1918"], (await Catalog(handler).ListAsync(ReleaseChannel.Stable, 10)).Select(r => r.Version.Text));
        Assert.Equal(["2026.9.12.721-beta"], (await Catalog(handler).ListAsync(ReleaseChannel.Beta, 10)).Select(r => r.Version.Text));
    }

    [Fact]
    public async Task Snapshot_ListsTheValidOnes_NewestFirst_WithTheirDownloadUrls()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Snapshot, 10);

        Assert.Equal(
            ["2026.9.20.0930-snapshot.g0123456", "2026.9.19.1615-snapshot.gabcdef1", "2026.9.19.1432-snapshot.g65615e7"],
            releases.Select(r => r.Version.Text));

        var oldest = releases[^1];
        const string baseUrl = "https://github.com/DbDataSync/DbDataSync/releases/download/snapshot-2026.9.19.1432-snapshot.g65615e7/";
        Assert.Equal(baseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg", oldest.PackageUrl!.AbsoluteUri);
        Assert.Equal(baseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg.sha512", oldest.ChecksumUrl!.AbsoluteUri);
        Assert.Equal("https://github.com/DbDataSync/DbDataSync/releases/tag/snapshot-2026.9.19.1432-snapshot.g65615e7", oldest.ReleaseUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task Snapshot_SkipsEveryReleaseThatIsNotAWellFormedSnapshot()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Snapshot, 100);

        // Only the three real ones survive: draft, non-prerelease, missing checksum, foreign host, wrong
        // asset name, scratch tag and a non-snapshot tag are all dropped, as are the stable/beta releases.
        Assert.Equal(3, releases.Count);
        Assert.DoesNotContain(releases, r => r.Version.Text.Contains("deadbee", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_SendsAUserAgent_AndAToken_OnlyWhenOneWasGiven()
    {
        var anonymous = Fixtures();
        await Catalog(anonymous).ListAsync(ReleaseChannel.Snapshot, 5);

        var sent = Assert.Single(anonymous.Sent);
        Assert.Equal(GitHubPage + "1", sent.Uri.AbsoluteUri);
        Assert.Equal("DbDataSync-update", sent.UserAgent);
        Assert.Contains("application/vnd.github+json", sent.Accept);
        Assert.Null(sent.Authorization);

        var authenticated = Fixtures();
        await Catalog(authenticated, token: "ghp_example").ListAsync(ReleaseChannel.Snapshot, 5);

        Assert.Equal("Bearer ghp_example", Assert.Single(authenticated.Sent).Authorization);
    }

    [Fact]
    public async Task NuGet_NeverCarriesTheGitHubToken()
    {
        var handler = Fixtures();
        await Catalog(handler, token: "ghp_example").ListAsync(ReleaseChannel.Stable, 5);

        Assert.Null(Assert.Single(handler.Sent).Authorization);
    }

    [Fact]
    public async Task Snapshot_StopsAtAShortPage_WithoutAskingForMore()
    {
        var handler = Fixtures();
        await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 50);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Snapshot_ReadsFurtherPagesUntilItHasEnough_ButNeverMoreThanThree()
    {
        // Every page is full (100 entries) of releases that are not snapshots, so the catalog can never
        // be satisfied and has to give up at its page ceiling rather than walk the whole repository.
        var fullPage = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{"tag_name":"2026.1.1.{{i + 1}}","draft":false,"prerelease":false,"assets":[]}""")) + "]";
        var handler = new FakeHttpHandler(_ => Http.Ok(fullPage));

        var releases = await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5);

        Assert.Empty(releases);
        Assert.Equal(
            [GitHubPage + "1", GitHubPage + "2", GitHubPage + "3"],
            handler.Sent.Select(s => s.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task Snapshot_FollowsToASecondPage_WhenTheFirstIsFullButHasTooFew()
    {
        var pageOne = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{"tag_name":"2026.1.1.{{i + 1}}","draft":false,"prerelease":false,"assets":[]}""")) + "]";
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith("page=1", StringComparison.Ordinal) ? Http.Ok(pageOne) : Http.Ok(Fixture.Read("github-releases.json")));

        var releases = await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 10);

        Assert.Equal(3, releases.Count);
        Assert.Equal(2, handler.Sent.Count);
    }

    [Fact]
    public async Task NuGet_ANon200_IsAnActionableError()
    {
        var handler = new FakeHttpHandler(_ => Http.Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Stable, 5));

        Assert.Contains("nuget.org answered 503", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"versions":"nope"}""")]
    [InlineData("""{"other":[]}""")]
    public async Task NuGet_AnUnexpectedBody_IsAnError_NotAnEmptyList(string body)
    {
        var handler = new FakeHttpHandler(_ => Http.Ok(body));

        await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Stable, 5));
    }

    [Fact]
    public async Task GitHub_AnUnexpectedBody_IsAnError()
    {
        var handler = new FakeHttpHandler(_ => Http.Ok("""{"message":"Not Found"}"""));

        await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));
    }

    [Fact]
    public async Task GitHub_RateLimited_SaysWhenItResets_AndNamesTheTokenVariable()
    {
        var reset = new DateTimeOffset(2026, 9, 19, 15, 42, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("60 requests an hour", ex.Message);
        Assert.Contains("15:42 UTC", ex.Message);
        Assert.Contains("GITHUB_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GitHub_RateLimited_WithAToken_DoesNotSuggestSettingOne()
    {
        var reset = new DateTimeOffset(2026, 9, 19, 15, 42, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler, token: "ghp_example").ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("15:42 UTC", ex.Message);
        Assert.DoesNotContain("GITHUB_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GitHub_ASecondaryLimit_ReportsTheRequestedPause()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "45");
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("45 seconds", ex.Message);
    }

    [Fact]
    public async Task GitHub_AForbiddenThatIsNotARateLimit_IsJustReportedAsAStatus()
    {
        var handler = new FakeHttpHandler(_ => Http.Status(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("GitHub answered 403", ex.Message);
    }

    [Fact]
    public async Task ANetworkFailure_NamesTheSourceThatCouldNotBeReached()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("Name or service not known"));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Stable, 5));

        Assert.Contains("Could not reach nuget.org", ex.Message);
        Assert.Contains("Name or service not known", ex.Message);
    }

    [Fact]
    public async Task ATimeout_IsAnError_ButACancellationIsNot()
    {
        var timedOut = new FakeHttpHandler(_ => throw new TaskCanceledException("timed out"));
        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(timedOut).ListAsync(ReleaseChannel.Stable, 5));
        Assert.Contains("did not answer in time", ex.Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 5, cancelled.Token));
    }

    [Fact]
    public void TheSourcesArePinnedConstants()
    {
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/dbdatasync/index.json", ReleaseSources.NuGetIndexUrl);
        Assert.Equal("https://api.github.com/repos/DbDataSync/DbDataSync/releases", ReleaseSources.GitHubReleasesUrl);
        Assert.Equal("DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg", ReleaseSources.SnapshotNupkgName(ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7")));
        Assert.EndsWith(".nupkg.sha512", ReleaseSources.SnapshotChecksumName(ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7")));
    }
}
