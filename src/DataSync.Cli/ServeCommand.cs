using DataSync.Api;
using LibGit2Sharp;

namespace DataSync.Cli;

/// <summary>
/// Starts the product: the API, the scheduler, and the web console, in one process.
/// <para>
/// It builds the <see cref="DataSyncHost"/> the API's own entry point builds — one composition root,
/// not two that drift the first time a service is registered in one of them.
/// </para>
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot;
        var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";

        try
        {
            Prepare(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
        {
            Console.Error.WriteLine($"Could not prepare the config repository at '{root}': {ex.Message}");
            return 1;
        }

        // Passed as configuration rather than mutated into the environment, so the same values reach
        // the host the same way they would from appsettings.json or an operator's own environment.
        var hostArgs = new List<string>(args.Where(a => !IsCliOnly(a)))
        {
            "--DataSync:RepoRoot", root,
            "--DataSync:StateDbPath", stateDb,
            "--urls", url,
        };

        var app = DataSyncHost.Build([.. hostArgs]);

        Console.WriteLine($"DataSync is starting.");
        Console.WriteLine($"  config repository  {Path.Combine(root, "config")}");
        Console.WriteLine($"  state database     {stateDb}");
        Console.WriteLine($"  console            {url}");

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Creates the config repository and initialises git in it.
    /// <para>
    /// Done here rather than left to the first save, because the config repo *is* a git repository —
    /// every write commits — and a first run that fails on "not a repository" is a first run that
    /// fails for a reason nobody installed a tool expecting.
    /// </para>
    /// </summary>
    private static void Prepare(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));

        if (!Repository.IsValid(root))
            Repository.Init(root);
    }

    /// <summary>Arguments this command consumes itself, which the host would otherwise see as its
    /// own and reject.</summary>
    private static bool IsCliOnly(string argument) =>
        argument is "--repo" or "--state-db" or "--url" || argument.StartsWith("--repo=", StringComparison.Ordinal);
}
