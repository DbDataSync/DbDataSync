using DbDataSync.Api.Configuration;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Services;

public sealed record PackageRefSummary(string Id, string Version);

/// <param name="Resolves">A real <see cref="LibraryRegistry.GetFactory"/> probe — the same check
/// <c>config library list</c> does — not just "the manifest parsed".</param>
/// <param name="UsedBy">Every descriptor driver (on disk, via <see cref="DriverDescriptorScanner"/>)
/// whose <c>library:</c> names this id. A compiled plugin never appears here — its package restores
/// privately, not through a shared library.</param>
/// <param name="Curated">Whether this id matches a bundled <see cref="KnownLibraries"/> entry, by
/// either its catalog id or its package id — an installed library can be keyed by either shape.</param>
/// <param name="PendingRestore">Phase 121: true when <c>library.json</c> exists but <c>lib/</c> does
/// not — an install that ran with no SDK available and no in-image catalog cache hit for it. Distinct
/// from <paramref name="Resolves"/> being false: that also covers a library whose <c>lib/</c> is
/// present but broken, which <c>config library sync</c> fixes the same way, but which isn't "waiting
/// on an SDK" the way this is.</param>
public sealed record LibrarySummary(
    string Id, IReadOnlyList<PackageRefSummary> Packages, string FactoryType, bool Resolves,
    IReadOnlyList<string> UsedBy, bool Curated, bool PendingRestore);

public sealed class LibrariesService(LibraryRegistry libraryRegistry, ApiOptions apiOptions)
{
    public IReadOnlyList<LibrarySummary> List()
    {
        var usedBy = DriverDescriptorScanner.Scan(apiOptions.RepoRoot)
            .Where(e => e.LibraryId is not null)
            .GroupBy(e => e.LibraryId!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.DriverId).OrderBy(x => x, StringComparer.Ordinal).ToList());

        return libraryRegistry.Installed.Values
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new LibrarySummary(
                m.Id,
                m.Packages.Select(p => new PackageRefSummary(p.Id, p.Version)).ToList(),
                m.FactoryType,
                Resolves(m.Id),
                usedBy.GetValueOrDefault(m.Id, []),
                KnownLibraries.TryGetByIdOrPackageId(m.Id) is not null,
                !Directory.Exists(LibraryPaths.LibDir(LibraryPaths.LibraryDir(apiOptions.RepoRoot, m.Id)))))
            .ToList();
    }

    /// <summary>Every descriptor driver on disk whose <c>library:</c> names <paramref name="libraryId"/>
    /// — what <c>DELETE /api/libraries/{id}</c> (phase 120) checks before refusing to remove one still
    /// in use.</summary>
    public IReadOnlyList<string> UsedBy(string libraryId) =>
        DriverDescriptorScanner.Scan(apiOptions.RepoRoot)
            .Where(e => e.LibraryId == libraryId)
            .Select(e => e.DriverId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private bool Resolves(string id)
    {
        try
        {
            libraryRegistry.GetFactory(id);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TypeLoadException)
        {
            return false;
        }
    }
}
