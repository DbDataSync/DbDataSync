using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Libraries;

/// <summary>The static compatibility check's result for one driver against its required, installed
/// library — phase 109j item 2/3. Never a hard failure signal by itself; every caller (<c>library
/// install</c>, <c>config check</c>, <c>ConnectionsController.Test</c>) turns
/// <see cref="Compatible"/> <c>false</c> into a warning, never a refusal.</summary>
public sealed record DriverLibraryCompatibilityResult(
    string DriverType, string LibraryId, string AssemblyName, bool Compatible, IReadOnlyList<string> MissingMembers);

/// <summary>
/// Phase 109j: the one entry point <c>library install</c>/<c>sync</c>, <c>config check</c>, and the
/// Test Connection flow all call — ties <see cref="IDriver.RequiredLibraryId"/>,
/// <see cref="LibraryRegistry"/>'s on-disk resolution, <see cref="LibrarySurfaceExtractor"/> (read the
/// driver's own compiled IL) and <see cref="LibrarySurfaceChecker"/> (check it against the installed
/// library, purely as metadata) into the one question every caller actually has: "is this driver's
/// required library, as installed right now, missing anything this driver's own code uses."
/// </summary>
public static class DriverLibraryCompatibility
{
    /// <summary>Null when there is nothing meaningful to check yet: <paramref name="driver"/> has no
    /// <see cref="IDriver.RequiredLibraryId"/> (a descriptor-driven/generic driver), the library isn't
    /// installed at all, or it's installed but still <c>PendingRestore</c> (no <c>lib/</c> on disk yet —
    /// <c>LibrariesAndDriversCheck</c> already reports that half of the problem; this check would have
    /// nothing to load).</summary>
    public static DriverLibraryCompatibilityResult? Check(IDriver driver, LibraryRegistry registry, string repoRoot)
    {
        var libraryId = driver.RequiredLibraryId;
        if (libraryId is null)
            return null;

        if (!registry.Installed.TryGetValue(libraryId, out var manifest))
            return null;

        var libDir = LibraryPaths.LibDir(LibraryPaths.LibraryDir(repoRoot, libraryId));
        if (!Directory.Exists(libDir))
            return null;

        var assemblyName = AssemblyNameFrom(manifest.FactoryType);
        var candidatePath = Path.Combine(libDir, assemblyName + ".dll");
        if (!File.Exists(candidatePath))
            return null;

        var driverAssemblyPath = driver.GetType().Assembly.Location;
        var used = LibrarySurfaceExtractor.ExtractUsedSurface(driverAssemblyPath, assemblyName);
        var check = LibrarySurfaceChecker.Check(used, libDir);

        return new DriverLibraryCompatibilityResult(driver.DriverType, libraryId, assemblyName, check.Compatible, check.MissingMembers);
    }

    /// <summary>Pulls the simple assembly name out of a manifest's assembly-qualified factory type
    /// (<c>"Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient"</c> →
    /// <c>"Microsoft.Data.SqlClient"</c>) — every <see cref="KnownLibraries"/> entry and every real
    /// installed manifest carries one, so this needs no separate lookup table.</summary>
    internal static string AssemblyNameFrom(string assemblyQualifiedFactoryType)
    {
        var commaIndex = assemblyQualifiedFactoryType.IndexOf(',');
        return commaIndex < 0
            ? assemblyQualifiedFactoryType
            : assemblyQualifiedFactoryType[(commaIndex + 1)..].Trim();
    }
}
