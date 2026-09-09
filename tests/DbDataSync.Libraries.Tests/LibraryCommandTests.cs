using DbDataSync.Cli;
using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>Drives <c>dbdatasync config library ...</c> exactly as an operator would, through
/// <see cref="LibraryCommand.RunAsync"/> rather than the lower-level <see cref="LibraryInstaller"/>
/// this exercises indirectly.</summary>
[Trait("Category", "Integration")]
public sealed class LibraryCommandTests : IAsyncLifetime
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-library-cmd-test-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        Console.SetOut(_output);
        Console.SetError(_error);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args) =>
        LibraryCommand.RunAsync([.. args, "--repo", _repoRoot]);

    [Fact]
    public async Task InstallThenList_ReportsTheInstalledLibrary()
    {
        Assert.Equal(0, await RunAsync("install", "MySqlConnector", "--version", "2.4.0"));

        _output.GetStringBuilder().Clear();
        Assert.Equal(0, await RunAsync("list"));

        Assert.Contains("MySqlConnector", _output.ToString());
        Assert.Contains("resolves", _output.ToString());
    }

    [Fact]
    public async Task Uninstall_RemovesTheLibraryDirectory()
    {
        await RunAsync("install", "MySqlConnector", "--version", "2.4.0");
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, "MySqlConnector");
        Assert.True(Directory.Exists(libraryDir));

        Assert.Equal(0, await RunAsync("uninstall", "MySqlConnector"));

        Assert.False(Directory.Exists(libraryDir));
    }

    [Fact]
    public async Task Sync_RebuildsLibFromTheManifestAlone()
    {
        await RunAsync("install", "MySqlConnector", "--version", "2.4.0");
        var libDir = LibraryPaths.LibDir(LibraryPaths.LibraryDir(_repoRoot, "MySqlConnector"));
        Directory.Delete(libDir, recursive: true);
        Assert.False(Directory.Exists(libDir));

        Assert.Equal(0, await RunAsync("sync", "MySqlConnector"));

        Assert.True(File.Exists(Path.Combine(libDir, "MySqlConnector.dll")));
    }

    [Fact]
    public async Task Install_WithoutAKnownFactoryType_RequiresOneExplicitly()
    {
        var exitCode = await RunAsync("install", "SomeUnknownPackage", "--version", "1.0.0");

        Assert.Equal(1, exitCode);
        Assert.Contains("--factory-type", _error.ToString());
    }

    [Fact]
    public async Task InstallByCatalogId_FillsPackageIdAndFactoryTypeFromTheCatalog()
    {
        Assert.Equal(0, await RunAsync("install", "mysql-connector", "--version", "2.4.0"));

        var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(LibraryPaths.LibraryDir(_repoRoot, "mysql-connector")));
        Assert.Equal("mysql-connector", manifest.Id);
        var package = Assert.Single(manifest.Packages);
        Assert.Equal("MySqlConnector", package.Id);
        Assert.Equal("2.4.0", package.Version);
        Assert.Equal("MySqlConnector.MySqlConnectorFactory, MySqlConnector", manifest.FactoryType);
    }
}
