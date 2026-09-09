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
    /// [--factory-type type] [--from mysql] [--display-name name]</c> for a YAML descriptor (109d), or
    /// <c>driver install &lt;id&gt; --kind compiled --package &lt;packageId&gt; --version &lt;v&gt;
    /// --assembly &lt;name.dll&gt; --driver-type &lt;FQTypeName&gt; [--source feed]</c> for a compiled
    /// plugin (109e) — restores the package into this driver's own <c>lib/</c> (not
    /// <c>providers/&lt;id&gt;/</c>: a compiled driver's package is private to it, not a shared
    /// provider another driver or the state store might also resolve) and writes a <c>driver.json</c>
    /// naming the assembly and the <see cref="Abstractions.IDriver"/> type to load from it.
    /// </summary>
    private static async Task<int> InstallAsync(string repoRoot, string[] args) =>
        string.Equals(CliOptions.Read(args, "--kind"), CompiledDriverManifest.CompiledKind, StringComparison.OrdinalIgnoreCase)
            ? await InstallCompiledAsync(repoRoot, args)
            : await InstallDescriptorAsync(repoRoot, args);

    private static async Task<int> InstallCompiledAsync(string repoRoot, string[] args)
    {
        var ids = StripFlagValues(args, "--kind", "--package", "--version", "--assembly", "--driver-type", "--source", "--repo")
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var packageId = CliOptions.Read(args, "--package");
        var version = CliOptions.Read(args, "--version");
        var assembly = CliOptions.Read(args, "--assembly");
        var driverType = CliOptions.Read(args, "--driver-type");
        var source = CliOptions.Read(args, "--source");

        if (ids.Count != 1 || packageId is null || version is null || assembly is null || driverType is null)
        {
            Console.Error.WriteLine(
                "Usage: dbdatasync driver install <id> --kind compiled --package <packageId> --version <v> " +
                "--assembly <name.dll> --driver-type <FQTypeName> [--source feed]");
            return 1;
        }

        var id = ids[0];
        var driverDir = Path.Combine(repoRoot, "drivers", id);
        var manifestPath = Path.Combine(driverDir, CompiledDriverManifest.FileName);
        if (File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"'{manifestPath}' already exists — `driver uninstall {id}` first to reinstall.");
            return 1;
        }

        Directory.CreateDirectory(driverDir);
        var package = new ProviderPackageRef(packageId, version);
        try
        {
            await ProviderInstaller.RestorePackagesAsync(Path.Combine(driverDir, "lib"), [package], source);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        new CompiledDriverManifest(id, CompiledDriverManifest.CompiledKind, assembly, driverType, [package])
            .Write(manifestPath);

        Console.WriteLine($"Installed compiled driver '{id}' ({packageId} {version}) and wrote '{manifestPath}'.");
        return 0;
    }

    /// <summary>
    /// <c>driver install &lt;id&gt; --provider &lt;packageId&gt; --version &lt;v&gt;
    /// [--factory-type type] [--from mysql] [--display-name name]</c>. Restores the provider exactly
    /// as <c>provider install</c> does, then writes a <c>driver.yaml</c> skeleton — filled in from a
    /// known starting template when <c>--from</c> names one, otherwise a minimal shell the operator
    /// fills in themselves (an empty <c>typeMap</c> maps every native type to <c>Unmappable</c>, which
    /// provisioning reports rather than guesses at, so an incomplete descriptor fails loud, not silently).
    /// </summary>
    private static async Task<int> InstallDescriptorAsync(string repoRoot, string[] args)
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
            var jsonPath = Path.Combine(dir, CompiledDriverManifest.FileName);

            if (File.Exists(yamlPath))
            {
                any = true;
                try
                {
                    var descriptor = DriverDescriptorReader.Read(yamlPath);
                    Console.WriteLine($"{descriptor.Id}  \"{descriptor.DisplayName}\"  [descriptor]  provider: {descriptor.Provider.Packages[0].Id}");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or YamlDotNet.Core.YamlException)
                {
                    Console.WriteLine($"{Path.GetFileName(dir)}  [FAILED TO PARSE: {ex.Message}]");
                }
            }
            else if (File.Exists(jsonPath))
            {
                any = true;
                try
                {
                    var manifest = CompiledDriverManifest.Read(jsonPath);
                    Console.WriteLine($"{manifest.Id}  [compiled]  assembly: {manifest.Assembly}  type: {manifest.DriverType}");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Console.WriteLine($"{Path.GetFileName(dir)}  [FAILED TO PARSE: {ex.Message}]");
                }
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
              dbdatasync driver install <id> --kind compiled --package <packageId> --version <v> --assembly <name.dll> --driver-type <FQTypeName> [--source feed]
              dbdatasync driver list
              dbdatasync driver uninstall <id>
            """);
    }
}
