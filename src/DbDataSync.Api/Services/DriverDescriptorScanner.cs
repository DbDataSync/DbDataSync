using DbDataSync.Drivers.Descriptor;

namespace DbDataSync.Api.Services;

/// <summary>Reads <c>&lt;repo&gt;/drivers/*</c> the way <see cref="DriverLoader"/> and the CLI's
/// <c>driver list</c> do, but for display only — never registers a driver, never throws on a bad
/// descriptor. A parse failure here is silently omitted from the result; <c>ReadinessChecks</c> (and
/// the CLI's own <c>driver list</c>) are what surface it as a problem to fix.</summary>
public static class DriverDescriptorScanner
{
    /// <param name="DriverId">The id the on-disk descriptor or manifest declares — expected (but not
    /// verified here) to match the <see cref="Drivers.Abstractions.IDriver.DriverType"/> the loader
    /// actually registered from the same directory.</param>
    /// <param name="Source">"descriptor" for a <c>driver.yaml</c>-backed driver, "compiled" for a
    /// <c>driver.json</c>-backed one.</param>
    /// <param name="LibraryId">The descriptor's <c>library:</c> reference — null for a compiled
    /// driver, which restores its package privately rather than through a shared library.</param>
    public sealed record Entry(string DriverId, string Source, string? LibraryId);

    public static IReadOnlyList<Entry> Scan(string repoRoot)
    {
        var driversRoot = Path.Combine(repoRoot, "drivers");
        if (!Directory.Exists(driversRoot))
            return [];

        var entries = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories(driversRoot))
        {
            var yamlPath = Path.Combine(dir, DriverLoader.DescriptorFileName);
            var jsonPath = Path.Combine(dir, CompiledDriverManifest.FileName);
            try
            {
                if (File.Exists(yamlPath))
                {
                    var descriptor = DriverDescriptorReader.Read(yamlPath);
                    entries.Add(new Entry(descriptor.Id, "descriptor", descriptor.Library));
                }
                else if (File.Exists(jsonPath))
                {
                    var manifest = CompiledDriverManifest.Read(jsonPath);
                    entries.Add(new Entry(manifest.Id, "compiled", null));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                or YamlDotNet.Core.YamlException or System.Text.Json.JsonException)
            {
                // Omitted, not surfaced — see the class doc comment.
            }
        }
        return entries;
    }
}
