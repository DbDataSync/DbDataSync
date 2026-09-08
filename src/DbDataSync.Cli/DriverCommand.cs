using DbDataSync.Drivers.Descriptor;
using DbDataSync.Providers;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync driver install|list|uninstall</c> — seeds a <c>driver.yaml</c> descriptor
/// (phase 109d) for a new SQL engine, restoring its provider along the way via
/// <see cref="ProviderInstaller"/>. See <c>architecture/planning/todo/nuget-loaded-drivers.md</c>
/// §*The descriptor*.
/// </summary>
public static class DriverCommand
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
            "list" => List(repoRoot),
            "uninstall" => Uninstall(repoRoot, rest),
            var other => Unknown(other),
        };
    }

    /// <summary>
    /// <c>driver install &lt;id&gt; --provider &lt;packageId&gt; --version &lt;v&gt;
    /// [--factory-type type] [--from mysql] [--display-name name]</c>. Restores the provider exactly
    /// as <c>provider install</c> does, then writes a <c>driver.yaml</c> skeleton — filled in from a
    /// known starting template when <c>--from</c> names one, otherwise a minimal shell the operator
    /// fills in themselves (an empty <c>typeMap</c> maps every native type to <c>Unmappable</c>, which
    /// provisioning reports rather than guesses at, so an incomplete descriptor fails loud, not silently).
    /// </summary>
    private static async Task<int> InstallAsync(string repoRoot, string[] args)
    {
        var ids = StripFlagValues(args, "--provider", "--version", "--factory-type", "--from", "--display-name", "--source", "--repo")
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (ids.Count != 1)
        {
            Console.Error.WriteLine(
                "Usage: dbdatasync driver install <id> --provider <packageId> --version <v> " +
                "[--factory-type type] [--from mysql] [--display-name name]");
            return 1;
        }

        var id = ids[0];
        var packageId = CliOptions.Read(args, "--provider");
        var version = CliOptions.Read(args, "--version");
        if (packageId is null || version is null)
        {
            Console.Error.WriteLine("Both --provider <packageId> and --version <v> are required.");
            return 1;
        }

        var factoryType = CliOptions.Read(args, "--factory-type") ?? KnownProviderFactories.TryGet(packageId);
        if (factoryType is null)
        {
            Console.Error.WriteLine(
                $"'{packageId}' has no known DbProviderFactory type. Pass one explicitly with --factory-type " +
                "\"Namespace.FactoryClass, AssemblyName\".");
            return 1;
        }

        var displayName = CliOptions.Read(args, "--display-name") ?? id;
        var package = new ProviderPackageRef(packageId, version);

        try
        {
            await ProviderInstaller.InstallAsync(repoRoot, packageId, [package], factoryType);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var driverDir = Path.Combine(repoRoot, "drivers", id);
        Directory.CreateDirectory(driverDir);
        var yamlPath = Path.Combine(driverDir, DriverLoader.DescriptorFileName);
        if (File.Exists(yamlPath))
        {
            Console.Error.WriteLine($"'{yamlPath}' already exists — edit it directly rather than overwriting it.");
            return 1;
        }

        var template = CliOptions.Read(args, "--from");
        var yaml = DriverTemplates.Render(template, id, displayName, factoryType, package);
        await File.WriteAllTextAsync(yamlPath, yaml);

        Console.WriteLine($"Installed provider '{packageId}' and wrote '{yamlPath}'.");
        Console.WriteLine(
            template is null
                ? "Fill in dialect and typeMap before this driver will do anything useful."
                : $"Seeded from the '{template}' template — review before relying on it.");
        return 0;
    }

    private static int List(string repoRoot)
    {
        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
        {
            Console.WriteLine("No drivers installed.");
            return 0;
        }

        var any = false;
        foreach (var dir in Directory.EnumerateDirectories(driversRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var yamlPath = Path.Combine(dir, DriverLoader.DescriptorFileName);
            if (!File.Exists(yamlPath))
                continue;

            any = true;
            try
            {
                var descriptor = DriverDescriptorReader.Read(yamlPath);
                Console.WriteLine($"{descriptor.Id}  \"{descriptor.DisplayName}\"  provider: {descriptor.Provider.Packages[0].Id}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or YamlDotNet.Core.YamlException)
            {
                Console.WriteLine($"{Path.GetFileName(dir)}  [FAILED TO PARSE: {ex.Message}]");
            }
        }

        if (!any)
            Console.WriteLine("No drivers installed.");
        return 0;
    }

    private static int Uninstall(string repoRoot, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync driver uninstall <id>");
            return 1;
        }

        var driverDir = Path.Combine(repoRoot, "drivers", args[0]);
        if (!Directory.Exists(driverDir))
        {
            Console.Error.WriteLine($"Driver '{args[0]}' is not installed.");
            return 1;
        }

        Directory.Delete(driverDir, recursive: true);
        Console.WriteLine(
            $"Uninstalled driver '{args[0]}'. Its provider is untouched — " +
            "`dbdatasync provider uninstall` separately if nothing else needs it.");
        return 0;
    }

    private static List<string> StripFlagValues(string[] args, params string[] flags)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (flags.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            result.Add(args[i]);
        }
        return result;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown driver subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              dbdatasync driver install <id> --provider <packageId> --version <v> [--factory-type type] [--from mysql] [--display-name name]
              dbdatasync driver list
              dbdatasync driver uninstall <id>
            """);
    }
}
