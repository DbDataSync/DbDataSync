using System.Reflection;
using System.Runtime.InteropServices;

namespace DbDataSync.Libraries;

public enum FactoryTypeDiscoveryStatus
{
    /// <summary>Exactly one qualifying type found — <see cref="FactoryTypeDiscoveryResult.FactoryType"/>
    /// is set.</summary>
    Found,
    /// <summary>No qualifying type found — the operator types the factory type by hand.</summary>
    NotFound,
    /// <summary>More than one qualifying type found — ambiguous, the operator types it by hand.</summary>
    Ambiguous,
}

/// <param name="Status">See <see cref="FactoryTypeDiscoveryStatus"/>.</param>
/// <param name="FactoryType">The assembly-qualified type name, set only when <paramref name="Status"/>
/// is <see cref="FactoryTypeDiscoveryStatus.Found"/>.</param>
public sealed record FactoryTypeDiscoveryResult(FactoryTypeDiscoveryStatus Status, string? FactoryType)
{
    public static readonly FactoryTypeDiscoveryResult NotFound = new(FactoryTypeDiscoveryStatus.NotFound, null);
    public static readonly FactoryTypeDiscoveryResult Ambiguous = new(FactoryTypeDiscoveryStatus.Ambiguous, null);
}

/// <summary>
/// Phase 122: for a library restored without an explicit <c>factoryType</c>, scans its restored
/// assemblies for a single public <see cref="System.Data.Common.DbProviderFactory"/> subclass — the
/// ADO.NET convention every bundled provider in <see cref="KnownLibraries"/> already follows — so the
/// operator can be handed a pre-filled guess instead of typing the assembly-qualified name by hand.
/// <para>
/// **Inspection-only.** Every assembly is loaded into a <see cref="MetadataLoadContext"/>, never the
/// process's own <see cref="System.Runtime.Loader.AssemblyLoadContext"/> — a malformed or hostile DLL
/// cannot run a module initializer or a static constructor during this scan, because
/// <see cref="MetadataLoadContext"/> never executes anything, only reads metadata. The trust boundary
/// stays where phase 120 put it: the confirmation an operator gives *before* the restore that produced
/// these files runs, not this scan of what the restore produced.
/// </para>
/// <para>
/// The resolver is just the restored closure's own DLLs plus the current runtime's shared framework
/// directory — no <c>.deps.json</c> parsing needed. Verified empirically against real restores of
/// <c>MySqlConnector</c>, <c>Npgsql</c> and <c>System.Data.SqlClient</c>: none collides by simple name
/// with anything under <see cref="RuntimeEnvironment.GetRuntimeDirectory"/>, and
/// <c>System.Data.Common</c> (where <c>DbProviderFactory</c> lives) always resolves from there.
/// </para>
/// <para>
/// A candidate must also declare a public static <c>Instance</c> field or property — every bundled
/// provider's own singleton shape — checked purely from metadata (no execution): this is what keeps an
/// incidental <c>DbProviderFactory</c> subclass with no working singleton from being offered as if it
/// were the real entry point.
/// </para>
/// </summary>
public static class FactoryTypeReflector
{
    private const string DbProviderFactoryAssembly = "System.Data.Common";
    private const string DbProviderFactoryTypeName = "System.Data.Common.DbProviderFactory";

    /// <summary>Scans every top-level <c>*.dll</c> in <paramref name="libDir"/> — a library's restored
    /// <c>lib/</c> directory; a native asset under <c>runtimes/&lt;rid&gt;/native/</c> is a level down
    /// and never itself a managed <c>*.dll</c> on the platforms that matter here.</summary>
    public static FactoryTypeDiscoveryResult Discover(string libDir)
    {
        var dllPaths = Directory.Exists(libDir)
            ? Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly)
            : [];
        if (dllPaths.Length == 0)
            return FactoryTypeDiscoveryResult.NotFound;

        var resolverPaths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Concat(dllPaths)
            .ToArray();
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));

        Type? factoryBaseType;
        try
        {
            factoryBaseType = mlc.LoadFromAssemblyName(DbProviderFactoryAssembly).GetType(DbProviderFactoryTypeName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return FactoryTypeDiscoveryResult.NotFound;
        }
        if (factoryBaseType is null)
            return FactoryTypeDiscoveryResult.NotFound;

        var matches = new List<(Type Type, string AssemblyName)>();
        foreach (var dllPath in dllPaths)
        {
            Assembly assembly;
            try
            {
                assembly = mlc.LoadFromAssemblyPath(dllPath);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                continue; // Not a managed assembly (or a second copy of one already loaded) — not a candidate.
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (!type.IsPublic || type.IsAbstract || !factoryBaseType.IsAssignableFrom(type))
                    continue;
                if (!DeclaresPublicStaticInstanceMember(type))
                    continue;

                matches.Add((type, assembly.GetName().Name!));
            }
        }

        return matches.Count switch
        {
            0 => FactoryTypeDiscoveryResult.NotFound,
            1 => new FactoryTypeDiscoveryResult(
                FactoryTypeDiscoveryStatus.Found, $"{matches[0].Type.FullName}, {matches[0].AssemblyName}"),
            _ => FactoryTypeDiscoveryResult.Ambiguous,
        };
    }

    private static bool DeclaresPublicStaticInstanceMember(Type type) =>
        type.GetField("Instance", BindingFlags.Public | BindingFlags.Static) is not null
        || type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) is not null;
}
