using DbDataSync.Api;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>
    /// Phase 136: everything from repo-root resolution through the moment <c>app.RunAsync()</c> starts
    /// serving is inside one outer <c>try</c> now — before this phase, an exception from
    /// <c>DbDataSyncHost.Build</c> or the first moments of <c>app.RunAsync()</c> propagated fully
    /// uncaught, with unverified behavior under a real Windows service (Event Viewer showed nothing but
    /// Service Control Manager's own generic "failed to start"/timeout entries). The narrow
    /// <c>Prepare()</c>-specific catch below is unchanged in what it catches and the message it builds —
    /// it is a second, more specific net inside the broader one, not replaced by it.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);

        try
        {
            // Phase 112: resolution fell through to the new machine-wide default, and the only real
            // configuration this install has ever had is still sitting at the old per-user one.
            // Printing and continuing would silently bootstrap a second, empty repo right next to a
            // working one — refuse instead, and name both paths.
            if (LegacyRootMigration.DetectAt(root) is { } legacyRoot)
            {
                Console.WriteLine(LegacyRootMigration.Message(legacyRoot, root));
                return 1;
            }

            var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
            // --url / DbDataSync__App__Url still override the file, exactly like every other
            // DbDataSync:* key (see DbDataSyncHost.InsertConfigFile) — the file is read here, rather
            // than through DbDataSyncHost.Build's own configuration chain, because the translation to
            // --urls, below, has to happen before that chain exists.
            var url = CliOptions.Read(args, "--url")
                ?? Environment.GetEnvironmentVariable("DbDataSync__App__Url")
                ?? DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:Url")
                ?? ApiOptions.DefaultUrl;

            try
            {
                Prepare(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
            {
                return Fail(ex, PrepareFailureMessage(root, ex));
            }

            await EnsureDuckDbInstalledAsync(root);

            var app = DbDataSyncHost.Build(BuildHostArgs(args, root, stateDb, url));

            // Phase 136: right now, under a service, there is no confirmation anywhere that startup
            // *completed* — only SCM's own Running status, which says nothing about whether the app
            // itself is healthy. One milestone, deliberately not every routine Console.WriteLine below —
            // turning the Application log into a duplicate console transcript would bury the one line
            // that matters.
            if (OperatingSystem.IsWindows()
                && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                // The OperatingSystem.IsWindows() guard above only covers this synchronous branch —
                // the platform-compat analyzer can't see through the delegate below, which runs later,
                // so it needs its own (here, always-true) guard to avoid a CA1416 warning.
                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    if (OperatingSystem.IsWindows())
                        WindowsServiceEventLog.WriteInformation($"DbDataSync started — console at {url}");
                });
            }

            Console.WriteLine($"DbDataSync is starting.");
            Console.WriteLine($"  config repository  {Path.Combine(root, "config")}");
            Console.WriteLine($"  state database     {stateDb}");
            Console.WriteLine($"  console            {url}");

            await app.RunAsync();

            // Phase 159: 75 when the service stopped itself so that an update can be applied — the unit
            // restarts it on that code (SuccessExitStatus/RestartForceExitStatus) and applies the update first.
            return app.Services.GetRequiredService<UpdateService>().RequestedExitCode ?? 0;
        }
        catch (Exception ex)
        {
            return Fail(ex, null);
        }
    }

    /// <summary>
    /// Where a startup failure actually goes: the Windows Event Log under a real service (nothing else
    /// is visible to an operator there), or <c>Console.Error</c> otherwise — interactive <c>serve</c>,
    /// or Linux/systemd, where an uncaught exception already reaches an operator via the terminal or
    /// (phase 136's own design doc, carried over rather than re-verified independently) systemd's
    /// default journal capture of a unit's stdout/stderr.
    /// </summary>
    /// <param name="message">A specific, already-built message (e.g. <see cref="PrepareFailureMessage"/>'s
    /// own output) when one exists; null falls back to <paramref name="exception"/>'s own type and
    /// message, formatted identically on both paths.</param>
    internal static int Fail(Exception exception, string? message)
    {
        if (OperatingSystem.IsWindows()
            && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            WindowsServiceEventLog.WriteError(exception, message);
        }
        else
        {
            Console.Error.WriteLine(message ?? $"{exception.GetType().Name}: {exception.Message}");
        }

        return 1;
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
        else
        {
            // A no-op for a fresh repo (nothing to migrate) or one already on phase 164's key names —
            // only prints/commits when there was real old-shaped content to rewrite.
            var migrated = LegacyConfigMigration.Migrate(root);
            if (migrated.Count > 0)
            {
                Console.WriteLine("dbdatasync.config.yaml used pre-phase-164 key names — migrated automatically:");
                foreach (var change in migrated)
                    Console.WriteLine($"  {change}");
            }
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

    /// <summary>
    /// Phase 135: a plain <c>ex.Message</c> here is usually the ownership-safety error this phase's own
    /// bug produced ("repository path ... is not owned by current user") — which names nothing about
    /// what the directory *was* set up for. A registered service's own account/platform, if
    /// <see cref="ServiceRegistration"/> has one recorded for <paramref name="root"/>, appends a second
    /// line naming it — a pure, separately-testable helper rather than inline in the catch block, since
    /// nothing here needs a real host to exercise.
    /// </summary>
    internal static string PrepareFailureMessage(string root, Exception ex)
    {
        var message = $"Could not prepare the config repository at '{root}': {ex.Message}";

        var registration = ServiceRegistration.Read(root);
        if (registration is not null)
        {
            message += $"\n  This data directory was registered for the '{registration.Account}' " +
                $"{registration.Platform} service account on {registration.RegisteredAtUtc:u} — check " +
                $"that it still owns '{root}'.";
        }

        return message;
    }

    /// <summary>Arguments this command consumes itself, which the host would otherwise see as its
    /// own and reject. Each takes a separate value token (<c>--repo X</c>, not <c>--repo=X</c>), which
    /// <see cref="BuildHostArgs"/> has to strip along with the flag itself.</summary>
    private static bool IsCliOnly(string argument) => argument is "--repo" or "--state-db" or "--url";

    /// <summary>
    /// What actually reaches <see cref="DbDataSyncHost.Build"/> — every CLI-only flag *and its value*
    /// stripped out, plus the resolved root/state-db path/URL passed through as ordinary configuration
    /// (rather than mutated into the environment), so the same values reach the host the same way they
    /// would from appsettings.json or an operator's own environment.
    /// <para>
    /// **Stripping only the flag token, leaving its value behind, is a real bug this method exists to
    /// fix** — found while extracting it for phase 164's own regression test. <c>--DbDataSync:*</c>
    /// arguments (<see cref="Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider"/>)
    /// parse key/value tokens by counting from the start of the array: an odd number of leftover,
    /// unrecognized non-flag tokens ahead of a real <c>--Key value</c> pair desyncs that counting, and
    /// the pair silently fails to parse at all — verified directly against the real provider. A single
    /// <c>dbdatasync serve --repo X</c> (no <c>--url</c>) left exactly one such orphan and would have
    /// silently dropped <em>every</em> <c>--DbDataSync:*</c> argument appended after it, App:RepoRoot
    /// and State:DbPath included — not merely a display glitch, a deployment silently running against
    /// the wrong repo. Stripping the flag's value token too removes the orphan instead of leaving one
    /// behind.
    /// </para>
    /// </summary>
    internal static string[] BuildHostArgs(string[] args, string root, string stateDb, string url)
    {
        var passthrough = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (IsCliOnly(args[i]))
            {
                i++; // also skip this flag's own value token
                continue;
            }

            if (args[i].StartsWith("--repo=", StringComparison.Ordinal))
                continue; // the one CLI-only flag with an inline value — no separate token to skip

            passthrough.Add(args[i]);
        }

        return
        [
            .. passthrough,
            "--DbDataSync:App:RepoRoot", root,
            "--DbDataSync:State:DbPath", stateDb,
            "--urls", url,
        ];
    }
}
