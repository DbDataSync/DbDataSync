using DbDataSync.Providers;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config provider install|sync|list|uninstall</c> — restores an ADO.NET provider package
/// DbDataSync does not reference at compile time into <c>&lt;repo&gt;/providers/&lt;id&gt;/</c>, so a
/// vendor's fix is a package swap, not a DbDataSync build. See
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The provider layer*.
/// </summary>
public static class ProviderCommand
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
    /// <c>provider install &lt;packageId&gt;[ &lt;packageId&gt;...] [--as &lt;id&gt;] [--version v]
    /// [--factory-type type] [--source feed]</c>. The first package id is the provider's own id unless
    /// <c>--as</c> names a different one (a multi-package provider whose primary assembly isn't first,
    /// or an id an operator wants to spell differently from the package).
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
                "Usage: dbdatasync config provider install <packageId>[ <packageId>...] [--as <id>] " +
                "[--version v] [--factory-type type] [--source feed]");
            return 1;
        }

        var id = CliOptions.Read(args, "--as") ?? packageIds[0];
        var version = CliOptions.Read(args, "--version");
        var source = CliOptions.Read(args, "--source");
        var factoryType = CliOptions.Read(args, "--factory-type") ?? KnownProviderFactories.TryGet(packageIds[0]);
        if (factoryType is null)
        {
            Console.Error.WriteLine(
                $"'{packageIds[0]}' has no known DbProviderFactory type. Pass one explicitly with --factory-type " +
                "\"Namespace.FactoryClass, AssemblyName\".");
            return 1;
        }

        if (version is null && packageIds.Count == 1)
        {
            Console.Error.WriteLine("Pass --version <version> — a provider install is pinned, never \"latest\".");
            return 1;
        }

        var packages = packageIds.Select(pid => new ProviderPackageRef(pid, version ?? "")).ToList();
        if (packages.Any(p => string.IsNullOrEmpty(p.Version)))
        {
            Console.Error.WriteLine("Every package needs a version; pass --version for a single-package install.");
            return 1;
        }

        try
        {
            var manifest = await ProviderInstaller.InstallAsync(repoRoot, id, packages, factoryType, source);
            Console.WriteLine($"Installed provider '{manifest.Id}' ({string.Join(", ", packages.Select(p => $"{p.Id} {p.Version}"))}).");
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
        var providersRoot = ProviderPaths.ProvidersDir(repoRoot);
        if (!Directory.Exists(providersRoot))
        {
            Console.WriteLine("No providers installed.");
            return 0;
        }

        var ids = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? [args[0]]
            : Directory.EnumerateDirectories(providersRoot)
                .Where(d => File.Exists(ProviderPaths.ManifestPath(d)))
                .Select(Path.GetFileName)
                .Cast<string>()
                .ToList();

        foreach (var id in ids)
        {
            await ProviderInstaller.SyncAsync(repoRoot, id);
            Console.WriteLine($"Synced provider '{id}'.");
        }

        return 0;
    }

    private static int List(string repoRoot)
    {
        var registry = new ProviderRegistry(repoRoot).LoadAll();
        if (registry.Installed.Count == 0)
        {
            Console.WriteLine("No providers installed.");
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

    private static bool TryResolves(ProviderRegistry registry, string id)
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
            Console.Error.WriteLine("Usage: dbdatasync config provider uninstall <id>");
            return 1;
        }

        var providerDir = ProviderPaths.ProviderDir(repoRoot, args[0]);
        if (!Directory.Exists(providerDir))
        {
            Console.Error.WriteLine($"Provider '{args[0]}' is not installed.");
            return 1;
        }

        Directory.Delete(providerDir, recursive: true);
        Console.WriteLine($"Uninstalled provider '{args[0]}'.");
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
        Console.Error.WriteLine($"Unknown provider subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              dbdatasync config provider install <packageId>[ <packageId>...] [--as <id>] --version <v> [--factory-type type] [--source feed]
              dbdatasync config provider sync [<id>]
              dbdatasync config provider list
              dbdatasync config provider uninstall <id>
            """);
    }
}
