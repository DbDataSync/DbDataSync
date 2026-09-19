using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DbDataSync.Updates;

/// <summary>
/// Lists what can be installed, per channel, newest first — from the two places
/// <see cref="ReleaseSources"/> pins, and nowhere else.
/// <para>
/// <c>Stable</c> and <c>Beta</c> are read from nuget.org's flat-container index. <c>Snapshot</c> is read from
/// this repository's GitHub releases: a release counts only if it is a published prerelease whose tag is
/// <see cref="ReleaseSources.SnapshotTagPrefix"/> plus a version that parses as a snapshot, **and** it carries
/// both assets a snapshot is made of, downloaded from under <see cref="ReleaseSources.SnapshotDownloadPrefix"/>.
/// Anything that fails a check is skipped, not reported — a release listing is not the place to discover
/// that a hand-made release exists.
/// </para>
/// </summary>
/// <param name="gitHubToken">Optional. Raises GitHub's limit from 60 requests an hour per address; sent only
/// with the release-listing request, never with a download.</param>
public sealed class ReleaseCatalog(HttpClient http, string? gitHubToken = null, string userAgent = "DbDataSync-update")
{
    private const int PageSize = 100;
    private const int MaxPages = 3;

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(
        ReleaseChannel channel, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var releases = channel == ReleaseChannel.Snapshot
            ? await ListSnapshotsAsync(limit, cancellationToken)
            : await ListNuGetAsync(channel, cancellationToken);

        return releases.OrderByDescending(r => r.Version).Take(limit).ToList();
    }

    private async Task<List<ReleaseInfo>> ListNuGetAsync(ReleaseChannel channel, CancellationToken cancellationToken)
    {
        using var request = NewRequest(ReleaseSources.NuGetIndexUrl);
        using var response = await SendAsync(request, "nuget.org", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ReleaseSourceException($"nuget.org answered {(int)response.StatusCode} when asked for the {ReleaseSources.PackageId} version list.");

        var versions = new List<ReleaseInfo>();
        using var document = await ReadJsonAsync(response, "nuget.org", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("versions", out var list)
            || list.ValueKind != JsonValueKind.Array)
            throw new ReleaseSourceException("nuget.org's version list was not in the expected shape.");

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && ReleaseVersion.TryParse(item.GetString(), out var version)
                && version.Channel == channel)
            {
                versions.Add(new ReleaseInfo(version, channel));
            }
        }

        return versions;
    }

    private async Task<List<ReleaseInfo>> ListSnapshotsAsync(int limit, CancellationToken cancellationToken)
    {
        var found = new List<ReleaseInfo>();

        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = NewRequest($"{ReleaseSources.GitHubReleasesUrl}?per_page={PageSize}&page={page}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrEmpty(gitHubToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);

            using var response = await SendAsync(request, "GitHub", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw GitHubFailure(response);

            using var document = await ReadJsonAsync(response, "GitHub", cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ReleaseSourceException("GitHub's release list was not in the expected shape.");

            var count = 0;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                count++;
                if (TryReadSnapshot(release, out var snapshot))
                    found.Add(snapshot);
            }

            if (found.Count >= limit || count < PageSize)
                break;
        }

        return found;
    }

    private static bool TryReadSnapshot(JsonElement release, out ReleaseInfo snapshot)
    {
        snapshot = null!;
        if (release.ValueKind != JsonValueKind.Object)
            return false;

        if (GetBool(release, "draft") || !GetBool(release, "prerelease"))
            return false;

        var tag = GetString(release, "tag_name");
        if (tag is null || !tag.StartsWith(ReleaseSources.SnapshotTagPrefix, StringComparison.Ordinal))
            return false;

        if (!ReleaseVersion.TryParse(tag[ReleaseSources.SnapshotTagPrefix.Length..], out var version)
            || version.Channel != ReleaseChannel.Snapshot
            || version.Text != tag[ReleaseSources.SnapshotTagPrefix.Length..])
            return false;

        Uri? packageUrl = null, checksumUrl = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (name is null || url is null || !url.StartsWith(ReleaseSources.SnapshotDownloadPrefix, StringComparison.Ordinal)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;

                if (name == ReleaseSources.SnapshotNupkgName(version))
                    packageUrl = uri;
                else if (name == ReleaseSources.SnapshotChecksumName(version))
                    checksumUrl = uri;
            }
        }

        if (packageUrl is null || checksumUrl is null)
            return false;

        Uri? page = Uri.TryCreate(GetString(release, "html_url"), UriKind.Absolute, out var html) ? html : null;
        snapshot = new ReleaseInfo(version, ReleaseChannel.Snapshot, packageUrl, checksumUrl, page);
        return true;
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string source, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Could not reach {source}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseSourceException($"{source} did not answer in time.", ex);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string source, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new ReleaseSourceException($"{source} answered with something that was not JSON.", ex);
        }
    }

    private ReleaseSourceException GitHubFailure(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (Header(response, "x-ratelimit-remaining") == "0"
                && long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                return new ReleaseSourceException(string.IsNullOrEmpty(gitHubToken)
                    ? $"GitHub's anonymous API limit (60 requests an hour) is used up until {when} UTC. " +
                      "Set GITHUB_TOKEN (or GH_TOKEN) to raise it, or try again then."
                    : $"GitHub's API rate limit for that token is used up until {when} UTC.");
            }

            if (int.TryParse(Header(response, "retry-after"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return new ReleaseSourceException($"GitHub asked for a pause of {seconds} seconds before the next request.");
        }

        return new ReleaseSourceException($"GitHub answered {(int)status} when asked for {ReleaseSources.Repository}'s releases.");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
