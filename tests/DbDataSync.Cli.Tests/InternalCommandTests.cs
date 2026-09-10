using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync internal build-catalog-cache</c> — the Dockerfile's own build step for phase 121's
/// runtime-only image. Restores every real <see cref="KnownLibraries"/> entry at its pinned version, so
/// this is <c>Category=Integration</c> like every other real-restore test in this repo.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InternalCommandTests : IAsyncLifetime
{
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-catalog-cache-test-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();

    public Task InitializeAsync()
    {
        Console.SetOut(_output);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        try { Directory.Delete(_outDir, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>Every catalog entry, for real, at its pinned version — the build-time assertion the
    /// phase doc itself asks for ("the catalog cache in the image matches every KnownLibraries entry
    /// at its pinned version").</summary>
    [Fact]
    public async Task BuildCatalogCache_RestoresEveryKnownLibrariesEntryAtItsPinnedVersion()
    {
        var exitCode = await InternalCommand.RunAsync(["build-catalog-cache", _outDir]);

        Assert.Equal(0, exitCode);
        foreach (var entry in KnownLibraries.All)
        {
            var libraryDir = LibraryPaths.LibraryDir(_outDir, entry.Id);
            Assert.True(File.Exists(LibraryPaths.ManifestPath(libraryDir)), $"'{entry.Id}' has no manifest.");
            var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
            Assert.Equal(entry.FactoryType, manifest.FactoryType);
            var package = Assert.Single(manifest.Packages);
            Assert.Equal(entry.PackageId, package.Id);
            Assert.Equal(entry.PinnedVersion, package.Version);
            Assert.True(Directory.EnumerateFiles(LibraryPaths.LibDir(libraryDir), "*.dll").Any(), $"'{entry.Id}' restored no DLLs.");
        }
    }

    /// <summary>What <see cref="LibraryInstaller.InstallOrDeferAsync"/> reads matches what this writes —
    /// the cache's own id-keyed <c>LibraryPaths.LibraryDir</c> shape, not a special layout of its own.</summary>
    [Fact]
    public async Task BuildCatalogCache_TheMySqlConnectorEntry_ResolvesAsARealFactory()
    {
        await InternalCommand.RunAsync(["build-catalog-cache", _outDir]);

        var registry = new LibraryRegistry(_outDir).LoadAll();
        Assert.Contains("mysql-connector", registry.Installed.Keys);
        Assert.NotNull(registry.GetFactory("mysql-connector"));
    }
}
