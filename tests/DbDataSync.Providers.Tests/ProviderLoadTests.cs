using System.Data.Common;
using DbDataSync.Providers;
using Xunit;

namespace DbDataSync.Providers.Tests;

/// <summary>
/// Proves the whole point: <c>MySqlConnector</c> is referenced by **no** <c>.csproj</c> in this
/// solution, yet <c>provider install</c> restores it, <see cref="ProviderRegistry"/> loads it, and a
/// factory it hands out opens a real connection to a MySQL container the build never compiled against.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProviderLoadTests : IAsyncLifetime
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-provider-test-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void MySqlConnector_IsReferencedByNoCsprojInTheSolution()
    {
        var repoRoot = FindRepoRoot();
        var csprojFiles = Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var path in csprojFiles)
            Assert.DoesNotContain("MySqlConnector", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_RestoresTheClosure_AndWritesTheManifest()
    {
        var manifest = await ProviderInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new ProviderPackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");

        Assert.Equal("MySqlConnector", manifest.Id);
        var providerDir = ProviderPaths.ProviderDir(_repoRoot, "MySqlConnector");
        Assert.True(File.Exists(ProviderPaths.ManifestPath(providerDir)));

        var libDir = ProviderPaths.LibDir(providerDir);
        var dlls = Directory.EnumerateFiles(libDir, "*.dll").Select(Path.GetFileName).ToList();
        Assert.Contains("MySqlConnector.dll", dlls);
        Assert.True(Directory.EnumerateFiles(libDir, "*.deps.json").Any());
    }

    [Fact]
    public async Task GetFactory_OpensARealConnectionToMySql_WithNoCompileTimeReference()
    {
        await ProviderInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new ProviderPackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");

        var registry = new ProviderRegistry(_repoRoot).LoadAll();
        Assert.Contains("MySqlConnector", registry.Installed.Keys);

        var factory = registry.GetFactory("MySqlConnector");
        await using DbConnection connection = factory.CreateConnection()!;
        connection.ConnectionString =
            "Server=localhost;Port=13306;User ID=root;Password=DbDataSync_Test_Pw1;Database=dbdatasync;";
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(1L, Convert.ToInt64(result));
    }

    [Fact]
    public void GetFactory_ForAnUninstalledProvider_NamesTheInstallCommand()
    {
        var registry = new ProviderRegistry(_repoRoot).LoadAll();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetFactory("NotInstalled"));

        Assert.Contains("dbdatasync config provider install", ex.Message);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
