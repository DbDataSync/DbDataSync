using System.Security.Cryptography;

namespace DbDataSync.Updates;

/// <param name="Directory">The folder to hand to <c>dotnet tool … --add-source</c> — holds this snapshot's
/// nupkg and nothing else that matters.</param>
/// <param name="Reused">True when a copy already staged was verified and used instead of downloading.</param>
public sealed record StagedSnapshot(string Directory, string NupkgPath, long SizeBytes, bool Reused);

/// <summary>
/// Downloads one snapshot's nupkg into <c>&lt;stage root&gt;/&lt;version&gt;/</c> and proves it arrived
/// intact, so the folder can be used as a NuGet source.
/// <para>
/// **What the check does and doesn't establish**: the SHA-512 sidecar is published in the same release as the
/// file it describes, so it catches a truncated or corrupted download, not a tampered release. What a snapshot
/// rests on beyond that is TLS to github.com and who can publish releases to this repository.
/// </para>
/// <para>
/// The package is streamed to a <c>.partial</c> file and only renamed once its hash matches, so an interrupted
/// run leaves nothing a later run could mistake for a good download; a mismatch deletes what was fetched.
/// A copy staged earlier is reused only after it verifies again — it is not trusted for having been there.
/// </para>
/// </summary>
public sealed class SnapshotStager(HttpClient http, string userAgent = "DbDataSync-update")
{
    private const int Sha512Bytes = 64;

    public async Task<StagedSnapshot> StageAsync(ReleaseInfo release, string stageRoot, CancellationToken cancellationToken = default)
    {
        if (release.Channel != ReleaseChannel.Snapshot || release.PackageUrl is null || release.ChecksumUrl is null)
            throw new ArgumentException("Only a snapshot release has a package to stage.", nameof(release));

        RequireGitHubReleaseUrl(release.PackageUrl);
        RequireGitHubReleaseUrl(release.ChecksumUrl);

        var directory = Path.Combine(stageRoot, release.Version.Text);
        var nupkgPath = Path.Combine(directory, ReleaseSources.SnapshotNupkgName(release.Version));
        var checksumPath = nupkgPath + ".sha512";
        var partialPath = nupkgPath + ".partial";

        Directory.CreateDirectory(directory);
        DeleteQuietly(partialPath);

        if (File.Exists(nupkgPath) && File.Exists(checksumPath))
        {
            var recorded = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Trim();
            if (await HashAsync(nupkgPath, cancellationToken) == recorded)
                return new StagedSnapshot(directory, nupkgPath, new FileInfo(nupkgPath).Length, Reused: true);

            DeleteQuietly(nupkgPath);
            DeleteQuietly(checksumPath);
        }

        var expected = await DownloadChecksumAsync(release.ChecksumUrl, cancellationToken);
        var actual = await DownloadPackageAsync(release.PackageUrl, partialPath, cancellationToken);

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            DeleteQuietly(partialPath);
            throw new ReleaseSourceException(
                $"The downloaded {ReleaseSources.SnapshotNupkgName(release.Version)} does not match its published SHA-512 checksum, " +
                "so it was discarded. Try again; if it keeps happening, the release itself may be bad.");
        }

        File.Move(partialPath, nupkgPath, overwrite: true);
        await File.WriteAllTextAsync(checksumPath, expected, cancellationToken);
        return new StagedSnapshot(directory, nupkgPath, new FileInfo(nupkgPath).Length, Reused: false);
    }

    private async Task<string> DownloadChecksumAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = NewRequest(url);
        using var response = await SendAsync(request, cancellationToken);
        var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

        // A SHA-512 is 64 bytes; anything that isn't base64 of exactly that is not a checksum, and comparing
        // against it would only ever "fail" for the wrong reason.
        try
        {
            if (Convert.FromBase64String(text).Length == Sha512Bytes)
                return text;
        }
        catch (FormatException)
        {
        }

        throw new ReleaseSourceException("The snapshot's .sha512 file did not contain a SHA-512 checksum.");
    }

    private async Task<string> DownloadPackageAsync(Uri url, string partialPath, CancellationToken cancellationToken)
    {
        using var request = NewRequest(url);
        using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            DeleteQuietly(partialPath);
            throw new ReleaseSourceException($"The download was interrupted: {ex.Message}", ex);
        }

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Could not reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseSourceException("GitHub did not answer in time.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ReleaseSourceException($"GitHub answered {status} when asked for {request.RequestUri!.Segments[^1]}.");
        }

        return response;
    }

    private HttpRequestMessage NewRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        return request;
    }

    private static void RequireGitHubReleaseUrl(Uri url)
    {
        if (!url.AbsoluteUri.StartsWith(ReleaseSources.SnapshotDownloadPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{url}' is not a release asset of {ReleaseSources.Repository}.", nameof(url));
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToBase64String(await SHA512.HashDataAsync(stream, cancellationToken));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
