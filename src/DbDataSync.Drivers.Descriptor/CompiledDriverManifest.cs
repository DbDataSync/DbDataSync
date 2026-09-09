using System.Text.Json;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// <c>&lt;repo&gt;/drivers/&lt;id&gt;/driver.json</c> — the compiled-plugin counterpart to
/// <c>driver.yaml</c>. Same directory, different manifest: <see cref="DriverLoader"/> tells the two
/// apart by which file is present, exactly the way <c>library.json</c> and this file both live under
/// one <c>lib/</c>-bearing directory layout without conflicting.
/// </summary>
/// <param name="Kind">Always <c>"compiled"</c> today — stated rather than assumed, so a future second
/// compiled shape (a <c>StateDialect</c> plugin, 109f/109h) can share this file name with its own
/// value instead of silently being misread as an <see cref="IDriver"/>.</param>
/// <param name="Assembly">The plugin's own main assembly file name (not a path — resolved under this
/// driver's <c>lib/</c>), e.g. <c>MyCompany.DbDataSync.Drivers.MySqlBinlog.dll</c>.</param>
/// <param name="DriverType">An assembly-qualified type name implementing <see cref="Abstractions.IDriver"/>,
/// resolved via <see cref="Type.GetType(string)"/> against the plugin's own
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> once its assembly is loaded.</param>
public sealed record CompiledDriverManifest(string Id, string Kind, string Assembly, string DriverType, IReadOnlyList<PackageRef> Packages)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static CompiledDriverManifest Read(string path) =>
        JsonSerializer.Deserialize<CompiledDriverManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"'{path}' does not contain a compiled driver manifest.");

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public const string FileName = "driver.json";
    public const string CompiledKind = "compiled";
}
