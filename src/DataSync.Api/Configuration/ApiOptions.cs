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

    /// <summary>
    /// Where the worker executable is, in the order the possibilities are actually true.
    /// <para>
    /// **Beside the running assembly first**, because that is what every published layout looks like —
    /// the tool, the container, and a plain <c>dotnet publish</c> all put the two in one directory.
    /// The dev-layout guess comes second and is kept only so this repo's own inner loop is unchanged:
    /// it swaps a <c>DataSync.Api/bin</c> segment for <c>DataSync.TaskRunner/bin</c>, which is true of
    /// this working tree and of nothing else, and was the only answer before.
    /// </para>
    /// <para>
    /// Not finding it returns the beside-the-assembly path anyway, so the failure names a real path
    /// somebody can look at rather than a plausible-looking one that never existed.
    /// </para>
    /// </summary>
    private static string ResolveDefaultTaskRunnerDllPath()
    {
        const string dll = "DataSync.TaskRunner.dll";

        var beside = Path.Combine(AppContext.BaseDirectory, dll);
        if (File.Exists(beside))
            return beside;

        var devLayout = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("DataSync.Api", "bin"),
                Path.Combine("DataSync.TaskRunner", "bin")),
            dll);

        return File.Exists(devLayout) ? devLayout : beside;
    }
}
