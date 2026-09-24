using System.Reflection;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// Enumerates <c>&lt;repo&gt;/drivers/*</c> and registers a driver for each — the one shared routine
/// both composition roots (<c>DbDataSyncHost.cs</c> and <c>TaskRunner/Program.cs</c>) call, so a
/// driver is available identically to the API and the worker without either duplicating the load logic
/// ("two composition roots", plan doc §*Blockers to clear*). A directory holding <c>driver.yaml</c> is
/// a descriptor (109d, <see cref="LoadDescriptorDrivers"/>); one holding <c>driver.json</c> is a
/// compiled plugin (109e, <see cref="LoadCompiledDrivers"/>) — the two never coexist in one directory,
/// but nothing stops one repo having some of each.
/// </summary>
public static class DriverLoader
{
    public const string DescriptorFileName = "driver.yaml";

    /// <summary>
    /// A failed descriptor is logged (via <paramref name="onError"/>, defaulting to stderr) and
    /// skipped rather than failing host startup — one operator's typo in a driver they added should
    /// not take down every other replication.
    /// </summary>
    public static void LoadDescriptorDrivers(
        string repoRoot, LibraryRegistry libraryRegistry, DriverRegistry driverRegistry,
        Action<string, Exception>? onError = null)
    {
        onError ??= (message, ex) => Console.Error.WriteLine($"{message}: {ex.Message}");

        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(driversRoot))
        {
            var yamlPath = Path.Combine(dir, DescriptorFileName);
            if (!File.Exists(yamlPath))
                continue;

            try
            {
                var descriptor = DriverDescriptorReader.Read(yamlPath);
                driverRegistry.RegisterWithRawQuery(DriverDescriptorReader.BuildDriver(descriptor, libraryRegistry, repoRoot));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                or YamlDotNet.Core.YamlException)
            {
                onError($"Failed to load driver descriptor '{yamlPath}'", ex);
            }
        }
    }

    /// <summary>
    /// A failed plugin — a missing dependency, a contract-version mismatch, a type that doesn't
    /// implement <see cref="IDriver"/> — is logged and skipped rather than failing host startup, for
    /// the same reason a failed descriptor is: one plugin's problem should not take every other driver
    /// down with it. Each plugin gets its own <see cref="DriverPluginLoadContext"/>, logged with its
    /// dependency closure and any assembly it had to down-bind to the host's version.
    /// </summary>
    public static void LoadCompiledDrivers(string repoRoot, DriverRegistry driverRegistry, Action<string, Exception>? onError = null)
    {
        onError ??= (message, ex) => Console.Error.WriteLine($"{message}: {ex.Message}");

        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(driversRoot))
        {
            var manifestPath = Path.Combine(dir, CompiledDriverManifest.FileName);
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                LoadOne(dir, manifestPath, driverRegistry, onError);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                or System.Text.Json.JsonException or ReflectionTypeLoadException or FileLoadException or BadImageFormatException)
            {
                onError($"Failed to load compiled driver '{manifestPath}'", ex);
            }
        }
    }

    private static void LoadOne(string driverDir, string manifestPath, DriverRegistry driverRegistry, Action<string, Exception> onError)
    {
        var manifest = CompiledDriverManifest.Read(manifestPath);
        if (manifest.Kind != CompiledDriverManifest.CompiledKind)
        {
            throw new NotSupportedException(
                $"Driver '{manifest.Id}': manifest kind '{manifest.Kind}' is not '{CompiledDriverManifest.CompiledKind}'.");
        }

        var assemblyPath = Path.Combine(driverDir, "lib", manifest.Assembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Driver '{manifest.Id}': assembly '{assemblyPath}' does not exist.", assemblyPath);

        var context = new DriverPluginLoadContext(
            manifest.Id, assemblyPath,
            onDownBind: message => onError($"Driver '{manifest.Id}'", new InvalidOperationException(message)));
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        var driverType = assembly.GetType(manifest.DriverType)
            ?? throw new InvalidOperationException(
                $"Driver '{manifest.Id}': type '{manifest.DriverType}' was not found in '{manifest.Assembly}'.");

        if (Activator.CreateInstance(driverType) is not IDriver driver)
        {
            throw new InvalidOperationException(
                $"Driver '{manifest.Id}': type '{manifest.DriverType}' does not implement IDriver.");
        }

        if (!DriverContract.IsSupported(driver.ContractVersion))
        {
            throw new NotSupportedException(
                $"Driver '{manifest.Id}' targets contract version {driver.ContractVersion}, which this build of " +
                $"DbDataSync (supporting {DriverContract.MinSupportedVersion}-{DriverContract.CurrentVersion}) cannot load. " +
                "Use a build of the plugin that targets this DbDataSync version.");
        }

        driverRegistry.RegisterWithRawQuery(driver);
    }
}
