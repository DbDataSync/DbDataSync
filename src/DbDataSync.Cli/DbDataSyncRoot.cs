using DbDataSync.Core.Config;

namespace DbDataSync.Cli;

/// <summary>
/// Where every <c>dbdatasync</c> command looks for its repo root — one resolver, used the same way by
/// <c>serve</c>, <c>invite</c> and <c>health</c> alike (phase 79).
/// <para>
/// **Explicit <c>--repo</c> wins outright.** Otherwise this walks upward from the current directory —
/// the same shape git itself uses to find <c>.git</c> — looking for <c>dbdatasync.config.yaml</c> at
/// each parent in turn, so a command run from anywhere inside a repo root finds it, not just from the
/// root itself. Otherwise <c>DbDataSync__RepoRoot</c> (phase 112), and only then
/// <see cref="CliOptions.DefaultRoot"/>.
/// </para>
/// <para>
/// Before this phase, each of <c>serve</c>/<c>invite</c>/<c>health</c> did its own
/// <c>CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot</c> independently, and none of them
/// searched — a command run from inside an existing repo, without <c>--repo</c>, found nothing there
/// and fell straight to the per-user default instead. This is new plumbing shared across all three,
/// not a tweak to what existed.
/// </para>
/// </summary>
public static class DbDataSyncRoot
{
    public static string Resolve(string[] args) => Resolve(args, Directory.GetCurrentDirectory());

    /// <summary>Takes the starting directory as a parameter so the walk-up logic is testable without
    /// changing the test process's own current directory.</summary>
    internal static string Resolve(string[] args, string startDirectory)
    {
        var explicitRoot = CliOptions.Read(args, "--repo");
        if (explicitRoot is not null)
            return explicitRoot;

        // DbDataSync__RepoRoot is not a new name invented for this resolver — it's the
        // environment-variable form of the DbDataSync:RepoRoot config key the raw API's own
        // configuration chain already honours (phase 112). serve/invite/health resolve the root
        // *before* that chain exists, so it has to be read directly here too, the same way
        // ServeCommand already reads DbDataSync__Url and InviteCommand reads DbDataSync__StateEngine.
        return FindUpward(startDirectory)
            ?? Environment.GetEnvironmentVariable("DbDataSync__RepoRoot")
            ?? CliOptions.DefaultRoot;
    }

    /// <summary>
    /// Walks from <paramref name="startDirectory"/> up through each parent looking for a
    /// <c>dbdatasync.config.yaml</c>. Stops at the filesystem root rather than at the user's home
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
            if (File.Exists(Path.Combine(directory.FullName, DbDataSyncConfigFile.FileName)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
