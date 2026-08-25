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

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataSync");

        var repoRoot = section["RepoRoot"] ?? Path.Combine(Directory.GetCurrentDirectory(), "datasync-repo");
        var stateDbPath = section["StateDbPath"] ?? Path.Combine(repoRoot, "state.db");
        var taskRunnerDllPath = section["TaskRunnerDllPath"] ?? ResolveDefaultTaskRunnerDllPath();

        return new ApiOptions { RepoRoot = repoRoot, StateDbPath = stateDbPath, TaskRunnerDllPath = taskRunnerDllPath };
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
