namespace DbDataSync.Updates;

/// <summary>
/// Every place an update can come from, as constants. Deliberately not configurable and not overridable
/// by a command-line option: a tool that will fetch a package and print the commands to install it must not
/// be pointable at an arbitrary URL, and a later phase lets the running service do the installing itself.
/// </summary>
public static class ReleaseSources
{
    public const string PackageId = "DbDataSync";

    /// <summary>The GitHub owner and repository releases are read from.</summary>
    public const string Repository = "DbDataSync/DbDataSync";

    /// <summary>nuget.org's flat-container index for <see cref="PackageId"/> — anonymous, and lists every
    /// version ever published, stable and prerelease alike.</summary>
    public const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/dbdatasync/index.json";

    public const string GitHubReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases";

    /// <summary>A snapshot release's tag is this plus its version. No slash, so a tag is always a single
    /// URL path segment, and never equal to a real release's bare-version tag.</summary>
    public const string SnapshotTagPrefix = "snapshot-";

    /// <summary>Every snapshot asset is downloaded from under this — anything else in a release listing is
    /// ignored rather than followed.</summary>
    public const string SnapshotDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    public static string SnapshotNupkgName(ReleaseVersion version) => $"{PackageId}.{version.Text}.nupkg";

    public static string SnapshotChecksumName(ReleaseVersion version) => SnapshotNupkgName(version) + ".sha512";
}
