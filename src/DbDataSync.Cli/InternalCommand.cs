using DbDataSync.Libraries;
using DbDataSync.Updates;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync internal ...</c> — commands for the build, not for an operator. Deliberately absent
/// from <see cref="Help.Print"/> and from every user-facing doc; the Dockerfile is the only caller.
/// </summary>
public static class InternalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync internal build-catalog-cache <out-dir>");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "build-catalog-cache" => await BuildCatalogCacheAsync(args[1..]),
            "apply-update" => await ApplyUpdateAsync(args[1..]),
            var other => Unknown(other),
        };
    }

    /// <summary>
    /// Phase 121: restores every <see cref="KnownLibraries"/> entry at its
    /// <see cref="LibraryCatalogEntry.PinnedVersion"/> into <paramref name="args"/>[0], shaped exactly
    /// like a repo's own <c>libraries/</c> directory (because it's the same <see cref="LibraryInstaller"/>
    /// writing it) — so <c>COPY</c>ing the result into the runtime-only image at
    /// <see cref="LibraryInstaller.DefaultCacheRoot"/> is all the Dockerfile needs to do, and
    /// <c>LibraryInstaller.InstallOrDeferAsync</c> reads it back with the same
    /// <see cref="LibraryPaths"/> helpers used to write it.
    /// </summary>
    private static async Task<int> BuildCatalogCacheAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync internal build-catalog-cache <out-dir>");
            return 1;
        }

        var outDir = args[0];
        Directory.CreateDirectory(outDir);

        foreach (var entry in KnownLibraries.All)
        {
            Console.WriteLine($"Caching '{entry.Id}' ({entry.PackageId} {entry.PinnedVersion})...");
            await LibraryInstaller.InstallAsync(
                outDir, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType);
        }

        Console.WriteLine($"Catalog cache built at '{outDir}' ({KnownLibraries.All.Count} entries).");
        return 0;
    }

    /// <summary>
    /// Phase 159: what the systemd unit runs as <c>ExecStartPre=+</c> before every start — applies an update the
    /// service asked for, or rolls back one that never became healthy.
    /// <para>
    /// **This runs as root and reads a file the unprivileged service wrote, so it trusts almost none of it.** The
    /// request names a version; everything else — which installation this is, what version is running, whether the
    /// version exists, the package itself — is found out here, from this process's own location and the pinned
    /// release sources. Its own records (the update on trial, the spare package, the log) are kept in a directory the
    /// service cannot write. See <see cref="PrivilegedContext"/> and <see cref="UpdateWorkspace"/>.
    /// </para>
    /// <para>
    /// **It does nothing unless it is root.** Run as anyone else there is no boundary it is protecting, and applying
    /// a request that whoever wrote it could have forged is exactly what it must not do; <c>--as-current-user</c>
    /// says the caller accepts that (for a machine where the service and the operator are one user, and for tests).
    /// </para>
    /// <para>
    /// **Always exits 0.** A failed update is recorded and logged, not thrown: this runs before the service starts,
    /// and a non-zero exit would stop the service starting at all, on whatever is installed. That would turn a failed
    /// update into an outage.
    /// </para>
    /// </summary>
    private static async Task<int> ApplyUpdateAsync(string[] args)
    {
        if (!Environment.IsPrivilegedProcess && !CliOptions.Has(args, "--as-current-user"))
        {
            Console.WriteLine(
                "update: not running as root, so nothing was applied. An update is applied by the service unit's privileged step " +
                "(installed with `sudo dbdatasync service install --self-update`).");
            return 0;
        }

        var root = DbDataSyncRoot.Resolve(args);
        try
        {
            var privilegedDirectory = CliOptions.Read(args, "--state-dir") ?? UpdateWorkspace.DefaultPrivilegedDirectory;
            var workspace = new UpdateWorkspace(root, privilegedDirectory);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var running = typeof(Help).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var userAgent = $"DbDataSync-update/{(ReleaseVersion.TryParse(running, out var version) ? version.Text : "unknown")}";
            var context = new PrivilegedContext(
                InstallLocator.Locate(
                    AppContext.BaseDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"),
                    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"),
                running,
                new ReleaseCatalog(http, gitHubToken: null, userAgent),
                new SnapshotStager(http, userAgent));

            var applier = new UpdateApplier(new UpdateStateStore(workspace), new ProcessToolCommandRunner(workspace));
            var result = await applier.ApplyPendingAsync(context, CancellationToken.None);
            if (result.Outcome != ApplyOutcome.NothingToDo)
                Console.WriteLine($"update: {result.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"update: could not run ({ex.GetType().Name}: {ex.Message}); starting on what is installed.");
        }

        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown internal subcommand '{sub}'.");
        return 1;
    }
}
