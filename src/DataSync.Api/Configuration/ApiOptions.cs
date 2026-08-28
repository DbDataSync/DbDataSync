namespace DataSync.Api.Configuration;

/// <summary>
/// Deployment-specific paths, read from the "DataSync" configuration section (appsettings.json,
/// environment variables, etc). RepoRoot/StateDbPath default to a local dev-friendly location so
/// "just run the API" works out of the box; TaskRunnerDllPath defaults to the sibling
/// DataSync.TaskRunner build output next to this project's own build output, which only holds for
/// this repo's own dev layout — override it for any real deployment.
/// </summary>
public sealed class ApiOptions
{
    public required string RepoRoot { get; init; }
    public required string StateDbPath { get; init; }
    public required string TaskRunnerDllPath { get; init; }

    /// <summary>
    /// The loopback-only port the runner-state endpoint listens on (phase 39). 0 — the default — binds
    /// an ephemeral one.
    /// <para>
    /// Ephemeral by default because nothing ever has to know this port in advance: the only clients are
    /// children this process spawns, and it tells each one the address it actually bound
    /// (<c>StateHost.BaseAddress</c>). A fixed default would buy nothing and cost a collision every
    /// time two instances run on one host — a developer's own API and a test run, say. It is settable
    /// for the one case that wants it: an operator who would rather firewall a known port than trust
    /// the loopback binding.
    /// </para>
    /// </summary>
    public int StatePort { get; init; }

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataSync");

        var repoRoot = section["RepoRoot"] ?? Path.Combine(Directory.GetCurrentDirectory(), "datasync-repo");
        var stateDbPath = section["StateDbPath"] ?? Path.Combine(repoRoot, "state.db");
        var taskRunnerDllPath = section["TaskRunnerDllPath"] ?? ResolveDefaultTaskRunnerDllPath();

        return new ApiOptions
        {
            RepoRoot = repoRoot,
            StateDbPath = stateDbPath,
            TaskRunnerDllPath = taskRunnerDllPath,
            StatePort = int.TryParse(section["StatePort"], out var statePort) ? statePort : 0,
        };
    }

    private static string ResolveDefaultTaskRunnerDllPath()
    {
        // Both projects build to src/<ProjectName>/bin/<Configuration>/<TFM>/ in this repo — swap the
        // project-name segment to find TaskRunner's output next to this project's own.
        var apiBinDir = AppContext.BaseDirectory;
        var taskRunnerBinDir = apiBinDir.Replace(
            Path.Combine("DataSync.Api", "bin"),
            Path.Combine("DataSync.TaskRunner", "bin"));
        return Path.Combine(taskRunnerBinDir, "DataSync.TaskRunner.dll");
    }
}
