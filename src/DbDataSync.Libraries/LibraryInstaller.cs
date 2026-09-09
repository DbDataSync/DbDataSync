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
    /// Installs (or reinstalls) one library. <paramref name="factoryType"/> defaults to
    /// <see cref="KnownLibraries"/>'s guess for <paramref name="packages"/>'s first entry when
    /// not given explicitly — the CLI is what requires one or the other.
    /// </summary>
    public static async Task<LibraryManifest> InstallAsync(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string factoryType,
        string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        await RestorePackagesAsync(LibraryPaths.LibDir(libraryDir), packages, nugetSource, cancellationToken);

        var manifest = new LibraryManifest(id, factoryType, packages);
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
    /// hand-editing a package version in <c>library.json</c>.</summary>
    public static async Task SyncAsync(string repoRoot, string id, CancellationToken cancellationToken = default)
    {
        var libraryDir = LibraryPaths.LibraryDir(repoRoot, id);
        var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
        await InstallAsync(repoRoot, id, manifest.Packages, manifest.FactoryType, cancellationToken: cancellationToken);
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
