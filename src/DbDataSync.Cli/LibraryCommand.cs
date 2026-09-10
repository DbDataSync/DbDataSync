using DbDataSync.Libraries;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config library install|sync|list|uninstall</c> — restores an ADO.NET library package
/// DbDataSync does not reference at compile time into <c>&lt;repo&gt;/libraries/&lt;id&gt;/</c>, so a
/// vendor's fix is a package swap, not a DbDataSync build. See
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The provider layer*.
/// </summary>
public static class LibraryCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var repoRoot = DbDataSyncRoot.Resolve(args);
        var rest = args[1..];

        return args[0].ToLowerInvariant() switch
        {
            "install" => await InstallAsync(repoRoot, rest),
            "sync" => await SyncAsync(repoRoot, rest),
            "list" => List(repoRoot),
            "uninstall" => Uninstall(repoRoot, rest),
            var other => Unknown(other),
        };
    }

    /// <summary>
    /// <c>library install &lt;packageId&gt;[ &lt;packageId&gt;...] [--as &lt;id&gt;] [--version v]
    /// [--factory-type type] [--source feed]</c>. The first package id is the library's own id unless
    /// <c>--as</c> names a different one (a multi-package library whose primary assembly isn't first,
    /// or an id an operator wants to spell differently from the package).
    /// <para>
    /// A single argument naming a <see cref="KnownLibraries"/> catalog id (<c>mysql-connector</c>) is
    /// shorthand for that entry's real package id and factory type — <c>--version</c> is still
    /// required (the "pinned, never latest" rule holds for a catalog install too), but
    /// <c>--factory-type</c> is not.
    /// </para>
    /// <para>
    /// Neither is it required for a non-catalog package: with no <c>--factory-type</c> and no catalog
    /// match, <see cref="LibraryInstaller.InstallAsync"/> restores the package first and then tries
    /// phase 122's reflection-assist against the result before giving up and requiring one explicitly.
    /// </para>
    /// </summary>
    private static async Task<int> InstallAsync(string repoRoot, string[] args)
    {
        // Options interleave with positional package ids in the raw args; strip every "--flag value"
        // pair to leave only the bare package ids. "--repo" is DbDataSyncRoot's own global option —
        // already consumed into repoRoot above, but still present in this method's own args slice.
        var packageIds = StripFlagValues(args, "--as", "--version", "--factory-type", "--source", "--repo")
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (packageIds.Count == 0)
        {
            Console.Error.WriteLine(
                "Usage: dbdatasync config library install <packageId>[ <packageId>...] [--as <id>] " +
                "[--version v] [--factory-type type] [--source feed]");
            return 1;
        }

        var id = CliOptions.Read(args, "--as") ?? packageIds[0];
        var version = CliOptions.Read(args, "--version");
        var source = CliOptions.Read(args, "--source");

        var catalogEntry = packageIds.Count == 1 ? KnownLibraries.TryGetById(packageIds[0]) : null;
        // Null is fine here — LibraryInstaller.InstallAsync tries phase 122's reflection-assist against
        // the restored closure before it requires --factory-type explicitly.
        var factoryType = CliOptions.Read(args, "--factory-type") ?? catalogEntry?.FactoryType ?? KnownLibraries.TryGet(packageIds[0]);

        if (version is null && packageIds.Count == 1)
        {
            Console.Error.WriteLine("Pass --version <version> — a library install is pinned, never \"latest\".");
            return 1;
        }

        var packages = catalogEntry is not null
            ? [new PackageRef(catalogEntry.PackageId, version ?? "")]
            : packageIds.Select(pid => new PackageRef(pid, version ?? "")).ToList();
        if (packages.Any(p => string.IsNullOrEmpty(p.Version)))
        {
            Console.Error.WriteLine("Every package needs a version; pass --version for a single-package install.");
            return 1;
        }

        try
        {
            var manifest = await LibraryInstaller.InstallAsync(repoRoot, id, packages, factoryType, source);
            Console.WriteLine($"Installed library '{manifest.Id}' ({string.Join(", ", packages.Select(p => $"{p.Id} {p.Version}"))}).");
            Console.WriteLine($"Factory: {manifest.FactoryType}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> SyncAsync(string repoRoot, string[] args)
    {
        var librariesRoot = LibraryPaths.LibrariesDir(repoRoot);
        if (!Directory.Exists(librariesRoot))
        {
            Console.WriteLine("No libraries installed.");
            return 0;
        }

        var ids = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? [args[0]]
            : Directory.EnumerateDirectories(librariesRoot)
                .Where(d => File.Exists(LibraryPaths.ManifestPath(d)))
                .Select(Path.GetFileName)
                .Cast<string>()
                .ToList();

        foreach (var id in ids)
        {
            await LibraryInstaller.SyncAsync(repoRoot, id);
            Console.WriteLine($"Synced library '{id}'.");
        }

        return 0;
    }

    private static int List(string repoRoot)
    {
        var registry = new LibraryRegistry(repoRoot).LoadAll();
        if (registry.Installed.Count == 0)
        {
            Console.WriteLine("No libraries installed.");
            return 0;
        }

        foreach (var manifest in registry.Installed.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var resolves = TryResolves(registry, manifest.Id);
            var packages = string.Join(", ", manifest.Packages.Select(p => $"{p.Id} {p.Version}"));
            Console.WriteLine($"{manifest.Id}  [{packages}]  factory: {manifest.FactoryType}  {(resolves ? "resolves" : "DOES NOT RESOLVE")}");
        }

        return 0;
    }

    private static bool TryResolves(LibraryRegistry registry, string id)
    {
        try
        {
            registry.GetFactory(id);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TypeLoadException)
        {
            return false;
        }
    }

    private static int Uninstall(string repoRoot, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync config library uninstall <id>");
            return 1;
        }

        var libraryDir = LibraryPaths.LibraryDir(repoRoot, args[0]);
        if (!Directory.Exists(libraryDir))
        {
            Console.Error.WriteLine($"Library '{args[0]}' is not installed.");
            return 1;
        }

        Directory.Delete(libraryDir, recursive: true);
        Console.WriteLine($"Uninstalled library '{args[0]}'.");
        return 0;
    }

    private static List<string> StripFlagValues(string[] args, params string[] flags)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++; // skip its value
                continue;
            }
            result.Add(args[i]);
        }
        return result;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown library subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              dbdatasync config library install <packageId>[ <packageId>...] [--as <id>] --version <v> [--factory-type type] [--source feed]
              dbdatasync config library sync [<id>]
              dbdatasync config library list
              dbdatasync config library uninstall <id>
            """);
    }
}
