using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DbDataSync.Libraries;

/// <summary>
/// Restores a library package (and its full transitive closure — managed dependencies and any
/// <c>runtimes/&lt;rid&gt;/native/</c> assets) into <c>&lt;repo&gt;/libraries/&lt;id&gt;/lib/</c>, using
/// nothing but the <c>dotnet</c> muxer <see cref="System.Diagnostics.ProcessStartInfo"/> already runs
/// everywhere else in this solution — no NuGet client library in the host.
/// <para>
/// The mechanism is a throwaway SDK-style class-library project referencing every package in the
/// manifest, published (not merely restored) so the SDK's own build copies the full dependency closure —
/// managed DLLs, <c>.deps.json</c>, and any native <c>runtimes/</c> assets — into one flat output
/// directory, which is copied verbatim into <c>lib/</c>.
/// </para>
/// </summary>
public static class LibraryInstaller
{
    /// <summary>
    /// Installs (or reinstalls) one library. <paramref name="factoryType"/> null means neither the
    /// caller nor <see cref="KnownLibraries"/> could name one up front — phase 122's reflection-assist
    /// then scans the freshly-restored closure for a single public
    /// <see cref="System.Data.Common.DbProviderFactory"/> subclass (see
    /// <see cref="FactoryTypeReflector"/>) before giving up. Restore always runs regardless: the
    /// closure has to exist on disk before there is anything to scan.
    /// </summary>
    public static async Task<LibraryManifest> InstallAsync(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string? factoryType,
        string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var libDir = LibraryPaths.LibDir(libraryDir);
        await RestorePackagesAsync(libDir, packages, nugetSource, cancellationToken);

        if (factoryType is null)
        {
            var discovery = FactoryTypeReflector.Discover(libDir);
            if (discovery.Status != FactoryTypeDiscoveryStatus.Found)
            {
                // Nothing usable under this id — clean up rather than leave a lib/ with no manifest,
                // the same "nothing installed" outcome any other failed install leaves behind.
                Directory.Delete(libraryDir, recursive: true);
                throw new InvalidOperationException(discovery.Status == FactoryTypeDiscoveryStatus.Ambiguous
                    ? $"'{id}' has more than one DbProviderFactory — pass factoryType explicitly."
                    : $"Could not find a DbProviderFactory in '{id}'. Pass factoryType explicitly.");
            }

            factoryType = discovery.FactoryType;
        }

        var manifest = new LibraryManifest(id, factoryType!, packages);
        manifest.Write(LibraryPaths.ManifestPath(libraryDir));
        return manifest;
    }

    /// <summary>
    /// The restore-then-copy half of <see cref="InstallAsync"/>, without a manifest — for a caller
    /// that writes a different manifest shape into the same directory layout. A compiled driver plugin
    /// (phase 109e) is exactly this: its package restores into <c>&lt;repo&gt;/drivers/&lt;id&gt;/lib/</c>
    /// rather than <c>libraries/&lt;id&gt;/lib/</c>, and its manifest is a <c>driver.json</c>, not a
    /// <c>library.json</c> — but the restore mechanics (publish, flatten, replace) are identical.
    /// </summary>
    public static async Task RestorePackagesAsync(
        string targetLibDir, IReadOnlyList<PackageRef> packages, string? nugetSource = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-library-{Guid.NewGuid():N}");
        var outDir = Path.Combine(tempDir, "out");
        Directory.CreateDirectory(tempDir);
        try
        {
            await PublishAsync(tempDir, outDir, packages, nugetSource, cancellationToken);

            // A fresh lib/ each install — a version downgrade or a dropped transitive dependency must
            // not leave a stale DLL from the previous version behind for the loader to pick up instead.
            if (Directory.Exists(targetLibDir))
                Directory.Delete(targetLibDir, recursive: true);
            Directory.CreateDirectory(targetLibDir);
            CopyAll(outDir, targetLibDir);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Re-runs the restore for an already-written manifest — a fresh deployment, or after
    /// hand-editing a package version in <c>library.json</c>. Also what completes phase 121's
    /// "pending restore" state: <see cref="InstallAsync"/> restores into <c>lib/</c> unconditionally,
    /// whether or not one was already there, so a manifest written with no <c>lib/</c> at all (because
    /// no SDK was available at install time) restores for the first time exactly the same way a
    /// re-sync of a normal library does.</summary>
    public static async Task SyncAsync(string repoRoot, string id, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
        await InstallAsync(repoRoot, id, manifest.Packages, manifest.FactoryType, cancellationToken: cancellationToken);
    }

    /// <summary>Phase 121: the outcome of <see cref="InstallOrDeferAsync"/>.</summary>
    public enum LibraryInstallOutcome
    {
        /// <summary>Restored for real via <c>dotnet publish</c> — the ordinary path, unchanged from
        /// before this phase.</summary>
        Installed,
        /// <summary>No SDK here to restore anything, but the exact package + pinned version was found
        /// in the in-image catalog cache and copied from there — no network, no SDK needed.</summary>
        InstalledFromCache,
        /// <summary>No SDK, and no cache hit — only <c>library.json</c> was written.
        /// <c>config library sync</c>, run wherever an SDK exists, completes it.</summary>
        PendingRestore,
    }

    /// <param name="Manifest">Written either way — even a <see cref="LibraryInstallOutcome.PendingRestore"/>
    /// needs its manifest committed so <c>config library sync</c> has something to read later.</param>
    public sealed record LibraryInstallResult(LibraryManifest Manifest, LibraryInstallOutcome Outcome);

    /// <summary>
    /// Phase 121's runtime-only-image entry point: on a host with the SDK, this is exactly
    /// <see cref="InstallAsync"/> (with phase 122's reflection-assist, since that needs a real
    /// restore). On a host with no SDK — the <c>-runtime</c> image — a catalog id at its
    /// <see cref="KnownLibraries"/>-pinned version is copied from <paramref name="cacheRoot"/> instead
    /// of restored; anything else (a non-catalog package, or a catalog one at a different version) can
    /// only have its manifest written, deferred as <see cref="LibraryInstallOutcome.PendingRestore"/>.
    /// <paramref name="factoryType"/> must be non-null on this path — reflection-assist needs a real
    /// restore to scan, which is exactly what isn't available here.
    /// </summary>
    /// <param name="hasSdkOverride">Defaults to the real <see cref="SdkAvailability.HasSdk()"/> — every
    /// production call site. A test drives the no-SDK branches with this instead of needing an actual
    /// runtime-only machine, the same seam phase 123 added for <c>Environment.ProcessPath</c>.</param>
    public static async Task<LibraryInstallResult> InstallOrDeferAsync(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string? factoryType,
        string? cacheRoot = null, string? nugetSource = null, bool? hasSdkOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (hasSdkOverride ?? SdkAvailability.HasSdk())
        {
            var manifest = await InstallAsync(repoRoot, id, packages, factoryType, nugetSource, cancellationToken);
            return new(manifest, LibraryInstallOutcome.Installed);
        }

        var cache = cacheRoot ?? DefaultCacheRoot;
        var catalogMatch = packages.Count == 1
            ? KnownLibraries.All.FirstOrDefault(e =>
                string.Equals(e.PackageId, packages[0].Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.PinnedVersion, packages[0].Version, StringComparison.OrdinalIgnoreCase))
            : null;
        var cacheLibDir = catalogMatch is null
            ? null
            : LibraryPaths.LibDir(LibraryPaths.LibraryDir(cache, catalogMatch.Id));

        if (cacheLibDir is not null && Directory.Exists(cacheLibDir))
        {
            var manifest = CopyFromCache(cacheLibDir, repoRoot, id, packages, factoryType ?? catalogMatch!.FactoryType);
            return new(manifest, LibraryInstallOutcome.InstalledFromCache);
        }

        if (factoryType is null)
        {
            throw new InvalidOperationException(
                $"'{id}' has no known DbProviderFactory type, and there is no SDK here to try reflection-assist. " +
                "Pass factoryType explicitly, or run `config library sync` on a host with the SDK.");
        }

        var pendingLibraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        Directory.CreateDirectory(pendingLibraryDir);
        var pendingManifest = new LibraryManifest(id, factoryType, packages);
        pendingManifest.Write(LibraryPaths.ManifestPath(pendingLibraryDir));
        return new(pendingManifest, LibraryInstallOutcome.PendingRestore);
    }

    /// <summary>Where <c>internal build-catalog-cache</c> writes and <see cref="InstallOrDeferAsync"/>
    /// reads by default — the runtime-only image's own copy of itself, so neither side needs new
    /// configuration to agree on it. Overridable (both directions) for tests.</summary>
    public const string DefaultCacheRoot = "/app/library-cache";

    private static LibraryManifest CopyFromCache(
        string cacheLibDir, string repoRoot, string id, IReadOnlyList<PackageRef> packages, string factoryType)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var libDir = LibraryPaths.LibDir(libraryDir);
        if (Directory.Exists(libDir))
            Directory.Delete(libDir, recursive: true);
        Directory.CreateDirectory(libDir);
        CopyAll(cacheLibDir, libDir);

        var manifest = new LibraryManifest(id, factoryType, packages);
        manifest.Write(LibraryPaths.ManifestPath(libraryDir));
        return manifest;
    }

    private static async Task PublishAsync(
        string tempDir, string outDir, IReadOnlyList<PackageRef> packages, string? nugetSource,
        CancellationToken cancellationToken)
    {
        var csprojPath = Path.Combine(tempDir, "library.csproj");
        var packageRefs = string.Join(
            Environment.NewLine, packages.Select(p => $"""    <PackageReference Include="{p.Id}" Version="{p.Version}" />"""));
        // A RuntimeIdentifier (framework-dependent, not self-contained) so a package with
        // runtimes/<rid>/native/ assets (SqlClient's SNI shim, DuckDB's libs) has its native binaries
        // for *this* machine copied into the flat publish output — a portable, RID-less publish skips
        // native assets entirely, which would silently strand exactly the packages this mechanism
        // exists to carry.
        var rid = RuntimeInformation.RuntimeIdentifier;
        await File.WriteAllTextAsync(csprojPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <SelfContained>false</SelfContained>
              </PropertyGroup>
              <ItemGroup>
            {packageRefs}
              </ItemGroup>
            </Project>
            """, cancellationToken);

        var args = new List<string> { "publish", csprojPath, "-c", "Release", "-o", outDir, "--nologo" };
        if (nugetSource is not null)
        {
            args.Add("--source");
            args.Add(nugetSource);
        }

        var (exitCode, output) = await RunDotnetAsync(args, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"`dotnet publish` failed restoring [{string.Join(", ", packages.Select(p => $"{p.Id} {p.Version}"))}]:\n{output}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        // Both streams awaited concurrently, not one after the other: a large enough dependency
        // closure can write enough to stderr (warnings) to fill the OS pipe buffer while this is still
        // reading stdout to completion, which deadlocks the child against the parent.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdoutTask.Result + stderrTask.Result);
    }

    private static void CopyAll(string sourceDir, string destDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }
}
