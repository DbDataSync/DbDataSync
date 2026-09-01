using DataSync.Core.Config;

namespace DataSync.Cli;

/// <summary>
/// Where every <c>datasync</c> command looks for its repo root — one resolver, used the same way by
/// <c>serve</c>, <c>invite</c> and <c>health</c> alike (phase 79).
/// <para>
/// **Explicit <c>--repo</c> wins outright.** Otherwise this walks upward from the current directory —
/// the same shape git itself uses to find <c>.git</c> — looking for <c>datasync.config.yaml</c> at
/// each parent in turn, so a command run from anywhere inside a repo root finds it, not just from the
/// root itself. Falls back to <see cref="CliOptions.DefaultRoot"/>, unchanged from every command's old
/// default, only when neither says otherwise.
/// </para>
/// <para>
/// Before this phase, each of <c>serve</c>/<c>invite</c>/<c>health</c> did its own
/// <c>CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot</c> independently, and none of them
/// searched — a command run from inside an existing repo, without <c>--repo</c>, found nothing there
/// and fell straight to the per-user default instead. This is new plumbing shared across all three,
/// not a tweak to what existed.
/// </para>
/// </summary>
public static class DataSyncRoot
{
    public static string Resolve(string[] args) => Resolve(args, Directory.GetCurrentDirectory());

    /// <summary>Takes the starting directory as a parameter so the walk-up logic is testable without
    /// changing the test process's own current directory.</summary>
    internal static string Resolve(string[] args, string startDirectory)
    {
        var explicitRoot = CliOptions.Read(args, "--repo");
        if (explicitRoot is not null)
            return explicitRoot;

        return FindUpward(startDirectory) ?? CliOptions.DefaultRoot;
    }

    /// <summary>
    /// Walks from <paramref name="startDirectory"/> up through each parent looking for a
    /// <c>datasync.config.yaml</c>. Stops at the filesystem root rather than at the user's home
    /// directory — the phase doc left this bound open as "whatever's simplest to implement correctly,"
    /// and the filesystem root needs no extra logic to detect (<see cref="DirectoryInfo.Parent"/> is
    /// null there) where a home-directory bound would need one more thing to get right for no benefit
    /// this tool's actual layouts need.
    /// </summary>
    private static string? FindUpward(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, DataSyncConfigFile.FileName)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
