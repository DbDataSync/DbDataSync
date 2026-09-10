using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// Phase 121's runtime-only-image entry point, <see cref="LibraryInstaller.InstallOrDeferAsync"/> —
/// every branch driven with <c>hasSdkOverride</c> rather than an actual runtime-only machine (this
/// sandbox always has the SDK), matching the seam phase 123 already established for
/// <c>Environment.ProcessPath</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibraryInstallOrDeferTests : IAsyncLifetime
{
    private static readonly LibraryCatalogEntry MySqlConnector = KnownLibraries.TryGetById("mysql-connector")!;

    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-installordefer-repo-{Guid.NewGuid():N}");
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-installordefer-cache-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_cacheRoot);
        // A real one-entry cache — the pinned catalog package, restored for real once, reused by
        // every "cache hit" test below rather than restoring it again per test.
        await LibraryInstaller.InstallAsync(
            _cacheRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, MySqlConnector.PinnedVersion)],
            MySqlConnector.FactoryType);
    }

    public Task DisposeAsync()
    {
        foreach (var dir in new[] { _repoRoot, _cacheRoot })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WithAnSdk_RestoresForReal_ExactlyLikeInstallAsync()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector", hasSdkOverride: true);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.Installed, result.Outcome);
        Assert.True(Directory.Exists(LibraryPaths.LibDir(LibraryPaths.LibraryDir(_repoRoot, "MySqlConnector"))));
    }

    [Fact]
    public async Task WithNoSdk_AndAPinnedCatalogVersion_CopiesFromTheCache()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, MySqlConnector.PinnedVersion)],
            factoryType: null, cacheRoot: _cacheRoot, hasSdkOverride: false);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.InstalledFromCache, result.Outcome);
        Assert.Equal(MySqlConnector.FactoryType, result.Manifest.FactoryType);
        var libDir = LibraryPaths.LibDir(LibraryPaths.LibraryDir(_repoRoot, MySqlConnector.Id));
        Assert.Contains("MySqlConnector.dll", Directory.EnumerateFiles(libDir).Select(Path.GetFileName));

        // A real factory out of the copy, not just files that happen to be there.
        var registry = new LibraryRegistry(_repoRoot).LoadAll();
        Assert.NotNull(registry.GetFactory(MySqlConnector.Id));
    }

    [Fact]
    public async Task WithNoSdk_AndAnUnpinnedVersionOfACatalogPackage_IsPendingRestore()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, MySqlConnector.Id, [new PackageRef(MySqlConnector.PackageId, "2.3.7")],
            MySqlConnector.FactoryType, cacheRoot: _cacheRoot, hasSdkOverride: false);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.PendingRestore, result.Outcome);
        Assert.Equal("2.3.7", Assert.Single(result.Manifest.Packages).Version);
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, MySqlConnector.Id);
        Assert.True(File.Exists(LibraryPaths.ManifestPath(libraryDir)));
        Assert.False(Directory.Exists(LibraryPaths.LibDir(libraryDir)));
    }

    [Fact]
    public async Task WithNoSdk_AndANonCatalogPackage_IsPendingRestore()
    {
        var result = await LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, "SomeVendor.Driver", [new PackageRef("SomeVendor.Driver", "1.0.0")],
            "SomeVendor.Driver.SomeVendorFactory, SomeVendor.Driver", cacheRoot: _cacheRoot, hasSdkOverride: false);

        Assert.Equal(LibraryInstaller.LibraryInstallOutcome.PendingRestore, result.Outcome);
        Assert.False(Directory.Exists(LibraryPaths.LibDir(LibraryPaths.LibraryDir(_repoRoot, "SomeVendor.Driver"))));
    }

    [Fact]
    public async Task WithNoSdk_NoCacheHit_AndNoFactoryType_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryInstaller.InstallOrDeferAsync(
            _repoRoot, "SomeVendor.Driver", [new PackageRef("SomeVendor.Driver", "1.0.0")],
            factoryType: null, cacheRoot: _cacheRoot, hasSdkOverride: false));

        Assert.Contains("no SDK", ex.Message);
    }
}
