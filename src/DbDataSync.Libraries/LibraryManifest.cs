using System.Text.Json;

namespace DbDataSync.Libraries;

/// <param name="Id">The name a consumer asks <see cref="LibraryRegistry.GetFactory"/> for, and the
/// directory name under <c>&lt;repo&gt;/libraries/</c>. Conventionally the primary NuGet package id
/// (<c>MySqlConnector</c>, <c>Microsoft.Data.SqlClient</c>).</param>
/// <param name="FactoryType">An assembly-qualified type name — <c>"MySqlConnector.MySqlConnectorFactory,
/// MySqlConnector"</c> — passed to <see cref="System.Data.Common.DbProviderFactories.RegisterFactory(string,string)"/>
/// verbatim. That overload resolves it lazily via <c>Type.GetType</c> on first use, so the assembly only
/// has to be *loadable* (see <see cref="LibraryRegistry"/>'s resolver), never referenced.</param>
/// <param name="Packages">A list, not a single id: a library can span assemblies or ship native-asset
/// packages that are not transitive dependencies of the primary one (Oracle, DB2).</param>
public sealed record LibraryManifest(string Id, string FactoryType, IReadOnlyList<PackageRef> Packages)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static LibraryManifest Read(string path) =>
        JsonSerializer.Deserialize<LibraryManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"'{path}' does not contain a library manifest.");

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <param name="Id">The NuGet package id.</param>
/// <param name="Version">Pinned exactly — <c>library sync</c> restores this version, not "latest".</param>
public sealed record PackageRef(string Id, string Version);
