namespace DbDataSync.Cli;

/// <summary>
/// Phase 112 moved <see cref="CliOptions.DefaultRoot"/> from a per-user location to a machine-wide
/// one. An upgrade whose only configuration lived at the old default must not silently look empty at
/// the new one — this is the check <c>serve</c>, <c>setup</c>, and <c>dbdatasync config check</c> all
/// run once they've resolved a root, so an operator who has never heard of this phase still finds out
/// what happened instead of ending up with two unrelated, empty-looking installs.
/// </summary>
internal static class LegacyRootMigration
{
    /// <summary>
    /// Null unless resolution fell all the way through to the platform default — no <c>--repo</c>, no
    /// walk-up hit, no <c>DbDataSync__App__RepoRoot</c> — the *new* default has no real configuration yet,
    /// and the *old* per-user location does. An operator who set any of those overrides has already
    /// made an explicit choice; second-guessing it here would be noise, not help.
    /// </summary>
    internal static string? DetectAt(string resolvedRoot) =>
        DetectAt(resolvedRoot, CliOptions.DefaultRoot, CliOptions.LegacyDefaultRoot);

    /// <summary>Takes the two locations as parameters so this is testable without touching the real,
    /// OS-defined machine-wide and per-user paths — the same reasoning
    /// <see cref="DbDataSyncRoot.Resolve(string[], string)"/> takes a start directory for.</summary>
    internal static string? DetectAt(string resolvedRoot, string defaultRoot, string legacyRoot)
    {
        if (resolvedRoot != defaultRoot)
            return null;

        if (ExistingSetup.DetectedAt(resolvedRoot))
            return null;

        return ExistingSetup.DetectedAt(legacyRoot) ? legacyRoot : null;
    }

    /// <summary>
    /// No auto-move: the folder can be large, a service account change may be involved, and a
    /// half-moved git repository is worse than a clear message naming both paths and how to keep
    /// using the old one deliberately.
    /// </summary>
    internal static string Message(string legacyRoot, string newRoot) =>
        $"""
        Found an existing DbDataSync configuration at
            {legacyRoot}
        The default is now {newRoot}. Either:
          - move that folder there, then re-run; or
          - keep it where it is: dbdatasync serve --repo "{legacyRoot}"
            (and pass the same --repo to `service install` / set DbDataSync__App__RepoRoot / set it in
            dbdatasync.config.yaml)
        """;
}
