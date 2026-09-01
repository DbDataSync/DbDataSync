using DataSync.Api;
using DataSync.Api.Auth;
using DataSync.Core.Config;
using DataSync.Core.Git;
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
        var root = DataSyncRoot.Resolve(args);
        var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
        // --url / DataSync__Url still override the file, exactly like every other DataSync:* key
        // (see DataSyncHost.InsertConfigFile) — the file is read here, rather than through
        // DataSyncHost.Build's own configuration chain, because the translation to --urls, below, has
        // to happen before that chain exists.
        var url = CliOptions.Read(args, "--url")
            ?? Environment.GetEnvironmentVariable("DataSync__Url")
            ?? DataSyncConfigFile.Read(root).GetValueOrDefault("DataSync:Url")
            ?? "http://localhost:5080";

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
    /// <para>
    /// A genuinely fresh root also gets a starter <c>datasync.config.yaml</c>, committed the same way
    /// every other config write is — never a repo root that already existed before this run, so
    /// re-running <c>serve</c> against a real deployment can never overwrite an operator's own edits,
    /// or their decision to delete the file and configure entirely by flag/environment variable.
    /// </para>
    /// </summary>
    internal static void Prepare(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var isFreshRepo = !Repository.IsValid(root);
        if (isFreshRepo)
            Repository.Init(root);

        if (isFreshRepo)
        {
            DataSyncConfigFile.WriteStarter(root);
            new GitCommitService(root).CommitChanges(
                [DataSyncConfigFile.PathIn(root)], "Add starter datasync.config.yaml", CurrentUser.SystemAuthor);
        }
    }

    /// <summary>Arguments this command consumes itself, which the host would otherwise see as its
    /// own and reject.</summary>
    private static bool IsCliOnly(string argument) =>
        argument is "--repo" or "--state-db" or "--url" || argument.StartsWith("--repo=", StringComparison.Ordinal);
}
