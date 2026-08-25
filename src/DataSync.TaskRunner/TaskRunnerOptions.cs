namespace DataSync.TaskRunner;

public sealed record TaskRunnerOptions(string RepoRoot, string StateDbPath, string Replication, Guid RunId)
{
    /// <summary>config/ lives at a fixed location under the git repo root — the same convention
    /// DataSync.Core.Config.ConfigPaths uses.</summary>
    public string ConfigRoot => Path.Combine(RepoRoot, "config");

    public static bool TryParse(string[] args, out TaskRunnerOptions? options, out string? error)
    {
        string? repoRoot = null;
        string? stateDbPath = null;
        string? replication = null;
        Guid? runId = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo-root" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--state-db" when i + 1 < args.Length:
                    stateDbPath = args[++i];
                    break;
                case "--replication" when i + 1 < args.Length:
                    replication = args[++i];
                    break;
                case "--run-id" when i + 1 < args.Length:
                    if (!Guid.TryParse(args[++i], out var parsedRunId))
                    {
                        options = null;
                        error = $"'--run-id' value '{args[i]}' is not a valid GUID.";
                        return false;
                    }
                    runId = parsedRunId;
                    break;
                default:
                    options = null;
                    error = $"Unrecognized argument '{args[i]}'.";
                    return false;
            }
        }

        var missing = new List<string>();
        if (repoRoot is null) missing.Add("--repo-root");
        if (stateDbPath is null) missing.Add("--state-db");
        if (replication is null) missing.Add("--replication");
        if (missing.Count > 0)
        {
            options = null;
            error = $"Missing required argument(s): {string.Join(", ", missing)}.";
            return false;
        }

        options = new TaskRunnerOptions(repoRoot!, stateDbPath!, replication!, runId ?? Guid.NewGuid());
        error = null;
        return true;
    }
}
