namespace DbDataSync.TaskRunner;

// No single --run-id anymore: work is claimed from the durable WorkQueue (one RunId minted per claimed
// item, not supplied externally) — see architecture/implementation/done/phase-008-work-queue-schema.md.
/// <param name="StateEndpoint">
/// The loopback address of the process that owns the state store. When absent this runner opens the
/// state file directly, which is the pre-phase-39 behaviour and is kept only for tools that run a
/// worker standalone — never for a runner the API spawned.
/// </param>
/// <param name="StateGraceSeconds">How long to keep retrying an unreachable owner before journalling
/// and shutting down. The owner is this process's parent, so a restart should be far shorter.</param>
public sealed record TaskRunnerOptions(
    string RepoRoot,
    string StateDbPath,
    string Replication,
    int DegreeOfParallelism = 4,
    string? StateEndpoint = null,
    int StateGraceSeconds = 60)
{
    /// <summary>config/ lives at a fixed location under the git repo root — the same convention
    /// DbDataSync.Core.Config.ConfigPaths uses.</summary>
    public string ConfigRoot => Path.Combine(RepoRoot, "config");

    public static bool TryParse(string[] args, out TaskRunnerOptions? options, out string? error)
    {
        string? repoRoot = null;
        string? stateDbPath = null;
        string? replication = null;
        int? degreeOfParallelism = null;
        string? stateEndpoint = null;
        int? graceSeconds = null;

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
                case "--state-endpoint" when i + 1 < args.Length:
                    stateEndpoint = args[++i];
                    break;
                case "--state-grace-seconds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedGrace) || parsedGrace < 0)
                    {
                        options = null;
                        error = $"'--state-grace-seconds' value '{args[i]}' must be a non-negative integer.";
                        return false;
                    }
                    graceSeconds = parsedGrace;
                    break;
                case "--degree-of-parallelism" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedDop) || parsedDop < 1)
                    {
                        options = null;
                        error = $"'--degree-of-parallelism' value '{args[i]}' must be a positive integer.";
                        return false;
                    }
                    degreeOfParallelism = parsedDop;
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

        options = new TaskRunnerOptions(
            repoRoot!, stateDbPath!, replication!, degreeOfParallelism ?? 4,
            // The endpoint may also arrive by environment, beside the token — see StateProtocol.
            stateEndpoint ?? Environment.GetEnvironmentVariable(DbDataSync.State.Remote.StateProtocol.EndpointEnvironmentVariable),
            graceSeconds ?? 60);
        error = null;
        return true;
    }
}
