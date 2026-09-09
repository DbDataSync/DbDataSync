using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// One <see cref="AssemblyLoadContext"/> per compiled driver plugin, named <c>driver:&lt;id&gt;</c> and
/// **not collectible** — a driver lives for the process; hot-reload is a non-goal, per the plan doc's
/// §*Assembly loading design*.
/// <para>
/// <see cref="Load"/> defers to <see cref="AssemblyLoadContext.Default"/> — by returning null — for any
/// assembly the default context has *already loaded*: <c>DbDataSync.Drivers.Abstractions</c>,
/// <c>DbDataSync.Core</c>, <c>System.Data.Common</c>, the framework. That is what makes the plugin's
/// <c>IDriver</c> the same <c>Type</c> as the host's, rather than a second, incompatible one — the
/// standard failure mode of a naïve isolated <c>AssemblyLoadContext</c>. Only a genuinely private
/// dependency — a driver-specific protocol library, a provider this driver bundles rather than shares —
/// resolves from the driver's own <c>lib/</c> directory.
/// </para>
/// <para>
/// **Known gap**: a provider (109c) the *shared* mechanism has not yet loaded into
/// <see cref="AssemblyLoadContext.Default"/> at the moment this context resolves it will load a private
/// copy here instead, which is a second <c>Type</c> from the one a later <c>provider install</c>-driven
/// load would produce. Not observed in practice (a driver is loaded after providers, per both
/// composition roots' ordering), but not structurally prevented either — see the phase's retrospective.
/// </para>
/// </summary>
public sealed class DriverPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly Action<string> _onDownBind;

    public DriverPluginLoadContext(string driverId, string mainAssemblyPath, Action<string>? onDownBind = null)
        : base($"driver:{driverId}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _onDownBind = onDownBind ?? (_ => { });
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var alreadyInDefault = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
        if (alreadyInDefault is not null)
        {
            var hostVersion = alreadyInDefault.GetName().Version;
            if (hostVersion is not null && assemblyName.Version is not null && hostVersion < assemblyName.Version)
            {
                _onDownBind(
                    $"'{assemblyName.Name}' {assemblyName.Version} was requested but the host only has " +
                    $"{hostVersion} loaded; using the host's version.");
            }
            return null; // defer to Default — same Type as the host's, which is the point.
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
