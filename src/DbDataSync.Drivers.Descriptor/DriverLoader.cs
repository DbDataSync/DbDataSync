using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Providers;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// Enumerates <c>&lt;repo&gt;/drivers/*/driver.yaml</c> and registers a <see cref="GenericDriver"/> for
/// each — the one shared routine both composition roots (<c>DbDataSyncHost.cs</c> and
/// <c>TaskRunner/Program.cs</c>) call, so a descriptor driver is available identically to the API and
/// the worker without either duplicating the load logic ("two composition roots", plan doc §*Blockers
/// to clear*). A compiled driver's <c>driver.json</c> (109e) lives in this same directory and is
/// ignored here — a counterpart loader for that shape is that phase's job.
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
        string repoRoot, ProviderRegistry providerRegistry, DriverRegistry driverRegistry,
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
                var providerId = descriptor.Provider.Packages[0].Id;
                var factory = providerRegistry.GetFactory(providerId);
                var spec = DriverDescriptorReader.ToSpec(descriptor, factory);
                driverRegistry.Register(new GenericDriver(spec));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                or YamlDotNet.Core.YamlException)
            {
                onError($"Failed to load driver descriptor '{yamlPath}'", ex);
            }
        }
    }
}
