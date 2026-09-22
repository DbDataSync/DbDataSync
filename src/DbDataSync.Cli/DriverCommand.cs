using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config driver install|list|uninstall</c> — seeds a <c>driver.yaml</c> descriptor
/// (phase 109d) for a new SQL engine, restoring its library along the way via
/// <see cref="LibraryInstaller"/>. See <c>architecture/planning/todo/nuget-loaded-drivers.md</c>
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
    /// <c>driver install &lt;id&gt; --library &lt;name&gt; --version &lt;v&gt;
    /// [--factory-type type] [--from mysql] [--display-name name]</c> for a YAML descriptor (109d), or
    /// <c>driver install &lt;id&gt; --kind compiled --package &lt;packageId&gt; --version &lt;v&gt;
    /// --assembly &lt;name.dll&gt; --driver-type &lt;FQTypeName&gt; [--source feed]</c> for a compiled
    /// plugin (109e) — restores the package into this driver's own <c>lib/</c> (not
    /// <c>libraries/&lt;id&gt;/</c>: a compiled driver's package is private to it, not a shared
    /// library another driver or the state store might also resolve) and writes a <c>driver.json</c>
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
                "Usage: dbdatasync config driver install <id> --kind compiled --package <packageId> --version <v> " +
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
        var package = new PackageRef(packageId, version);
        try
        {
            await LibraryInstaller.RestorePackagesAsync(Path.Combine(driverDir, "lib"), [package], source);
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
    /// <c>driver install &lt;id&gt; [--library &lt;name&gt;] --version &lt;v&gt;
    /// [--factory-type type] [--from &lt;knownDriverId&gt;] [--display-name name]</c>. Installs the
    /// named library exactly as <c>library install</c> would (a <c>--library</c> naming a
    /// <see cref="KnownLibraries"/> catalog id resolves to that entry's real package id and factory
    /// type, same shorthand <c>LibraryCommand</c> accepts) — or, when the name already resolves in
    /// <see cref="LibraryRegistry"/>, reuses it rather than reinstalling (trusting the
    /// already-installed library's own <c>factoryType</c> over a mismatched
    /// <c>--factory-type</c>/<see cref="KnownLibraries"/> guess) — then writes a <c>driver.yaml</c>
    /// skeleton naming it: a <see cref="KnownDrivers"/> entry's own body when <c>--from</c> names one
    /// (whose bound library id also becomes <c>--library</c>'s default when it's omitted), otherwise a
    /// minimal shell the operator fills in themselves.
    /// </summary>
    private static async Task<int> InstallDescriptorAsync(string repoRoot, string[] args)
    {
        var ids = StripFlagValues(args, "--library", "--version", "--factory-type", "--from", "--display-name", "--source", "--repo")
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (ids.Count != 1)
        {
            Console.Error.WriteLine(
                "Usage: dbdatasync config driver install <id> [--library <name>] --version <v> " +
                "[--factory-type type] [--from <knownDriverId>] [--display-name name]");
            return 1;
        }

        var id = ids[0];

        KnownDriverEntry? knownDriver = null;
        var template = CliOptions.Read(args, "--from");
        if (template is not null)
        {
            knownDriver = KnownDrivers.TryGetById(template);
            if (knownDriver is null)
            {
                Console.Error.WriteLine(
                    $"No starter template named '{template}'. Known templates: " +
                    $"{string.Join(", ", KnownDrivers.All.Select(d => d.Id))}. Omit --from for a minimal shell.");
                return 1;
            }
        }

        var requestedLibraryName = CliOptions.Read(args, "--library") ?? knownDriver?.BoundLibraryId;
        var version = CliOptions.Read(args, "--version");
        if (requestedLibraryName is null || version is null)
        {
            Console.Error.WriteLine(
                "--version <v> is required, and so is --library <name> unless --from names a catalog " +
                "entry (its bound library is then the default).");
            return 1;
        }

        var displayName = CliOptions.Read(args, "--display-name") ?? id;

        var catalogLibrary = KnownLibraries.TryGetById(requestedLibraryName);
        // A library's id is always its real NuGet package id — a catalog id like "mysql-connector"
        // (whether typed via --library or defaulted from --from's own bound library) is shorthand for
        // typing the package id, never a name the install itself gets keyed under (see
        // architecture/planning/todo/follow-up-library-install-paths-disagree-on-the-resulting-library-id.md).
        var libraryName = catalogLibrary?.PackageId ?? requestedLibraryName;

        var registry = new LibraryRegistry(repoRoot).LoadAll();
        if (registry.Installed.ContainsKey(libraryName))
        {
            Console.WriteLine($"Reusing already-installed library '{libraryName}'.");
        }
        else
        {
            var factoryType = CliOptions.Read(args, "--factory-type") ?? catalogLibrary?.FactoryType ?? KnownLibraries.TryGet(libraryName);
            if (factoryType is null)
            {
                Console.Error.WriteLine(
                    $"'{libraryName}' has no known DbProviderFactory type. Pass one explicitly with --factory-type " +
                    "\"Namespace.FactoryClass, AssemblyName\".");
                return 1;
            }

            var package = new PackageRef(libraryName, version);
            try
            {
                await LibraryInstaller.InstallAsync(repoRoot, libraryName, [package], factoryType);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        var driverDir = Path.Combine(repoRoot, "drivers", id);
        Directory.CreateDirectory(driverDir);
        var yamlPath = Path.Combine(driverDir, DriverLoader.DescriptorFileName);
        if (File.Exists(yamlPath))
        {
            Console.Error.WriteLine($"'{yamlPath}' already exists — edit it directly rather than overwriting it.");
            return 1;
        }

        var yaml = knownDriver is not null
            ? KnownDrivers.Render(knownDriver, id, displayName, libraryName)
            : DriverTemplates.Minimal(id, displayName, libraryName);
        await File.WriteAllTextAsync(yamlPath, yaml);

        Console.WriteLine($"Installed library '{libraryName}' and wrote '{yamlPath}'.");
        Console.WriteLine(
            knownDriver is null
                ? "Fill in dialect and typeMap before this driver will do anything useful."
                : $"Seeded from the '{knownDriver.Id}' template — review before relying on it.");
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
                    Console.WriteLine($"{descriptor.Id}  \"{descriptor.DisplayName}\"  [descriptor]  library: {descriptor.Library}");
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
            Console.Error.WriteLine("Usage: dbdatasync config driver uninstall <id>");
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
            $"Uninstalled driver '{args[0]}'. Its library is untouched — " +
            "`dbdatasync config library uninstall` separately if nothing else needs it.");
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
              dbdatasync config driver install <id> [--library <name>] --version <v> [--factory-type type] [--from <knownDriverId>] [--display-name name]
              dbdatasync config driver install <id> --kind compiled --package <packageId> --version <v> --assembly <name.dll> --driver-type <FQTypeName> [--source feed]
              dbdatasync config driver list
              dbdatasync config driver uninstall <id>
            """);
    }
}
