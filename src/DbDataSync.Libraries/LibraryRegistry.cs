using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DbDataSync.Libraries;

/// <summary>
/// Loads every <c>&lt;repo&gt;/libraries/*/library.json</c> manifest and registers its
/// <see cref="DbProviderFactory"/> by name, so any consumer — the state store, a driver, a script —
/// gets one without the solution ever referencing the underlying NuGet package. See
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The provider layer*.
/// <para>
/// **Registration is the string overload** —
/// <see cref="DbProviderFactories.RegisterFactory(string, string)"/> — which does a lazy
/// <c>Type.GetType()</c> on first <see cref="DbProviderFactories.GetFactory(string)"/>, so the
/// assembly only has to be *loadable* at that point, never referenced by anything in this solution.
/// </para>
/// <para>
/// **Loadable** means: this registers one <see cref="AssemblyLoadContext.Default"/>
/// <see cref="AssemblyLoadContext.Resolving"/> handler per process (see <see cref="EnsureResolverArmed"/>)
/// backed by an <see cref="AssemblyDependencyResolver"/> per installed library directory, so a
/// library's own managed dependencies and <c>runtimes/&lt;rid&gt;/native/</c> assets resolve. Libraries
/// load into the *default* context deliberately, not a per-library isolated one — a <c>SqlConnection</c>
/// from a library and one from a future built-in driver must be the same <c>Type</c>, which an isolated
/// context would break.
/// </para>
/// </summary>
public sealed class LibraryRegistry
{
    private static readonly List<AssemblyDependencyResolver> Resolvers = [];
    private static bool _resolverArmed;
    private static readonly object ResolverLock = new();

    private readonly string _librariesRoot;

    public LibraryRegistry(string repoRoot) => _librariesRoot = LibraryPaths.LibrariesDir(repoRoot);

    /// <summary>Every installed library's manifest, id-keyed. Populated by <see cref="LoadAll"/>;
    /// empty (not an error) for a repo with no <c>libraries/</c> directory at all.</summary>
    public IReadOnlyDictionary<string, LibraryManifest> Installed { get; private set; } =
        new Dictionary<string, LibraryManifest>(StringComparer.Ordinal);

    /// <summary>
    /// Scans <c>libraries/*/library.json</c>, arms assembly resolution for each one found, and
    /// registers every factory. An absent or empty <c>libraries/</c> directory is a silent no-op — most
    /// deployments have none.
    /// </summary>
    public LibraryRegistry LoadAll()
    {
        var installed = new Dictionary<string, LibraryManifest>(StringComparer.Ordinal);
        if (Directory.Exists(_librariesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(_librariesRoot))
            {
                var manifestPath = LibraryPaths.ManifestPath(dir);
                if (!File.Exists(manifestPath))
                    continue;

                var manifest = LibraryManifest.Read(manifestPath);
                installed[manifest.Id] = manifest;

                var libDir = LibraryPaths.LibDir(dir);
                if (Directory.Exists(libDir))
                    ArmResolver(libDir);

                DbProviderFactories.RegisterFactory(manifest.Id, manifest.FactoryType);
            }
        }

        Installed = installed;
        return this;
    }

    /// <summary>
    /// Loads and registers exactly one library already on disk (by id) into this same instance's
    /// <see cref="Installed"/>, without rescanning — for a caller (phase 120's <c>POST /api/libraries</c>)
    /// that just wrote it and wants this process's own view of "is it installed" to reflect that
    /// immediately, without a restart. Deliberately not "call <see cref="LoadAll"/> again": that would
    /// re-arm a resolver for every already-installed library a second time, unboundedly, if this were
    /// ever called more than once per process — which, unlike <see cref="LoadAll"/>'s one call at
    /// startup, this now can be. A silent no-op if <paramref name="id"/> isn't actually on disk.
    /// </summary>
    public void RegisterInstalled(string id)
    {
        var libraryDir = Path.Combine(_librariesRoot, id);
        var manifestPath = LibraryPaths.ManifestPath(libraryDir);
        if (!File.Exists(manifestPath))
            return;

        var manifest = LibraryManifest.Read(manifestPath);

        var libDir = LibraryPaths.LibDir(libraryDir);
        if (Directory.Exists(libDir))
            ArmResolver(libDir);

        DbProviderFactories.RegisterFactory(manifest.Id, manifest.FactoryType);
        Installed = new Dictionary<string, LibraryManifest>(Installed, StringComparer.Ordinal) { [manifest.Id] = manifest };
    }

    /// <summary>Drops <paramref name="id"/> from <see cref="Installed"/> — for
    /// <c>DELETE /api/libraries/{id}</c> (phase 120), so this process's own view stops claiming a
    /// removed library is installed. Does not (cannot) un-arm its resolver or un-register its factory
    /// name; an orphaned resolver pointing at a deleted directory is harmless dead weight, not a
    /// correctness problem — <see cref="GetFactory"/> refuses by <see cref="Installed"/> membership
    /// first, before ever reaching <see cref="DbProviderFactories.GetFactory(string)"/>.</summary>
    public void Remove(string id)
    {
        if (!Installed.ContainsKey(id))
            return;

        Installed = Installed.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="DbProviderFactories.GetFactory(string)"/>, with a message that names the fix
    /// (<c>dbdatasync config library install</c>) instead of the BCL's generic "no factory registered".
    /// </summary>
    public DbProviderFactory GetFactory(string id)
    {
        if (!Installed.ContainsKey(id))
            throw new InvalidOperationException(
                $"Library '{id}' is not installed. Install it with `dbdatasync config library install {id}`.");

        return DbProviderFactories.GetFactory(id);
    }

    private static void ArmResolver(string libDir)
    {
        var depsJson = Directory.EnumerateFiles(libDir, "*.deps.json").FirstOrDefault();
        if (depsJson is null)
            return;

        // AssemblyDependencyResolver keys off the .deps.json's own main assembly path, not the
        // directory — any file in the same publish output resolves the same closure.
        var resolver = new AssemblyDependencyResolver(depsJson);
        lock (ResolverLock)
        {
            Resolvers.Add(resolver);
            EnsureResolverArmed();
        }
    }

    /// <summary>
    /// One <see cref="AssemblyLoadContext.Resolving"/> handler for the process's lifetime, trying every
    /// armed library's resolver in turn. Registered once — a second <see cref="LibraryRegistry"/>
    /// instance (a test standing up its own repo root, say) adds to <see cref="Resolvers"/> rather than
    /// double-subscribing.
    /// </summary>
    private static void EnsureResolverArmed()
    {
        if (_resolverArmed)
            return;
        _resolverArmed = true;

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, unmanagedName) =>
        {
            lock (ResolverLock)
            {
                foreach (var resolver in Resolvers)
                {
                    var path = resolver.ResolveUnmanagedDllToPath(unmanagedName);
                    if (path is not null)
                        return NativeLibrary.Load(path);
                }
            }
            return IntPtr.Zero;
        };

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            lock (ResolverLock)
            {
                foreach (var resolver in Resolvers)
                {
                    var path = resolver.ResolveAssemblyToPath(name);
                    if (path is not null)
                        return context.LoadFromAssemblyPath(path);
                }
            }
            return null;
        };
    }
}

/// <summary>The on-disk layout every library (and, from 109d/109e on, every driver) shares — one
/// place both the installer and the registry agree on it.</summary>
public static class LibraryPaths
{
    public const string ManifestFileName = "library.json";
    public const string LibDirName = "lib";

    public static string LibrariesDir(string repoRoot) => Path.Combine(repoRoot, "libraries");
    public static string LibraryDir(string repoRoot, string id) => Path.Combine(LibrariesDir(repoRoot), id);
    public static string ManifestPath(string libraryDir) => Path.Combine(libraryDir, ManifestFileName);
    public static string LibDir(string libraryDir) => Path.Combine(libraryDir, LibDirName);
}
