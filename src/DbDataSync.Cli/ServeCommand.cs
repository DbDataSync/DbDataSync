using DbDataSync.Api;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Libraries;
using LibGit2Sharp;

namespace DbDataSync.Cli;

/// <summary>
/// Starts the product: the API, the scheduler, and the web console, in one process.
/// <para>
/// It builds the <see cref="DbDataSyncHost"/> the API's own entry point builds — one composition root,
/// not two that drift the first time a service is registered in one of them.
/// </para>
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);

        // Phase 112: resolution fell through to the new machine-wide default, and the only real
        // configuration this install has ever had is still sitting at the old per-user one. Printing
        // and continuing would silently bootstrap a second, empty repo right next to a working one —
        // refuse instead, and name both paths.
        if (LegacyRootMigration.DetectAt(root) is { } legacyRoot)
        {
            Console.WriteLine(LegacyRootMigration.Message(legacyRoot, root));
            return 1;
        }

        var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
        // --url / DbDataSync__Url still override the file, exactly like every other DbDataSync:* key
        // (see DbDataSyncHost.InsertConfigFile) — the file is read here, rather than through
        // DbDataSyncHost.Build's own configuration chain, because the translation to --urls, below, has
        // to happen before that chain exists.
        var url = CliOptions.Read(args, "--url")
            ?? Environment.GetEnvironmentVariable("DbDataSync__Url")
            ?? DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:Url")
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

        // Fired off rather than awaited: a Windows service must report SERVICE_RUNNING to SCM
        // within its start-pending timeout, and this method's own `dotnet publish` restore can
        // easily run longer than that on a fresh install — the documented service-install flow
        // (docs/install.md) never runs an interactive `serve`/`setup` first, so this is routinely
        // the very first restore LocalSystem's own (cold) NuGet cache has ever done. The method
        // already treats its own failure as non-fatal ("replication itself is unaffected" below),
        // so nothing downstream needs this to have finished before the host starts listening.
        _ = EnsureDuckDbInstalledAsync(root);

        // Passed as configuration rather than mutated into the environment, so the same values reach
        // the host the same way they would from appsettings.json or an operator's own environment.
        var hostArgs = new List<string>(args.Where(a => !IsCliOnly(a)))
        {
            "--DbDataSync:RepoRoot", root,
            "--DbDataSync:StateDbPath", stateDb,
            "--urls", url,
        };

        var app = DbDataSyncHost.Build([.. hostArgs]);

        Console.WriteLine($"DbDataSync is starting.");
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
    /// A genuinely fresh root also gets a starter <c>dbdatasync.config.yaml</c>, committed the same way
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
            DbDataSyncConfigFile.WriteStarter(root);
            new GitCommitService(root).CommitChanges(
                [DbDataSyncConfigFile.PathIn(root)], "Add starter dbdatasync.config.yaml", CurrentUser.SystemAuthor);
        }
    }

    /// <summary>
    /// Phase 109i: DuckDB backs verification's result paging and every DuckDB-kind segmenting strategy
    /// regardless of which replication engines this deployment ever configures — unlike 109h's two
    /// providers, there is no "engine chosen" operator decision to hang the install off, so this
    /// installs it unconditionally, here, on every <c>serve</c> start. This is also the one call site
    /// both <c>dbdatasync serve</c> directly and <c>setup</c>'s own Start button reach —
    /// <see cref="Tui.SetupScreen"/>'s Start button already calls <see cref="RunAsync"/> — so one change
    /// covers both entry points.
    /// <para>
    /// Idempotent by a plain directory check, not a full <see cref="LibraryRegistry.LoadAll"/> — cheap
    /// enough to run unconditionally on every start, and it means an ordinary restart after the first
    /// one does no network/restore work at all: <see cref="LibraryInstaller.InstallAsync"/> is only ever
    /// reached the first time.
    /// </para>
    /// <para>
    /// Best-effort: a failed install is logged and this command still starts, the same posture
    /// <c>CertificateExpiryService</c> already takes for "this shouldn't be allowed to take the whole
    /// process down" — verification and DuckDB-backed segmenting degrade if DuckDB never installs, but
    /// replication itself never depended on it.
    /// </para>
    /// </summary>
    internal static async Task EnsureDuckDbInstalledAsync(string root)
    {
        const string libraryId = "duckdb";
        var libDir = LibraryPaths.LibDir(LibraryPaths.LibraryDir(root, libraryId));
        if (Directory.Exists(libDir))
            return;

        try
        {
            var catalogEntry = KnownLibraries.TryGetById(libraryId)!;
            await LibraryInstaller.InstallAsync(
                root, libraryId, [new PackageRef(catalogEntry.PackageId, catalogEntry.PinnedVersion)],
                catalogEntry.FactoryType);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not install '{libraryId}' automatically: {ex.Message}. Verification and " +
                "DuckDB-backed segmenting strategies will not work until `dbdatasync config library " +
                "sync` completes it; replication itself is unaffected.");
        }
    }

    /// <summary>Arguments this command consumes itself, which the host would otherwise see as its
    /// own and reject.</summary>
    private static bool IsCliOnly(string argument) =>
        argument is "--repo" or "--state-db" or "--url" || argument.StartsWith("--repo=", StringComparison.Ordinal);
}
