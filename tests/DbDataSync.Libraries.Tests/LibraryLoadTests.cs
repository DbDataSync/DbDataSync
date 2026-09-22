using System.Data.Common;
using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// Proves the point phase 109c established: <c>library install</c> restores <c>MySqlConnector</c>,
/// <see cref="LibraryRegistry"/> loads it, and a factory it hands out opens a real connection to a MySQL
/// container, all with no compile-time reference in this solution.
/// <para>
/// **Phase 147 changed one half of that**: <c>DbDataSync.Drivers.MySql.csproj</c> now carries a real
/// <c>PackageReference Include="MySqlConnector" ... ExcludeAssets="runtime"</c> — the built-in driver
/// needs the typed provider API at compile time, the same way <c>DbDataSync.Drivers.MsSql.csproj</c>
/// references <c>Microsoft.Data.SqlClient</c>. <c>ExcludeAssets="runtime"</c> is what keeps the actual
/// invariant this class demonstrates true: the assembly still isn't physically shipped by that
/// reference, and still has to be loadable at first touch through an installed library's armed resolver
/// (phase 144's <c>BuiltInDriverLibraries</c>) — a compile-time reference to the typed API, not a
/// runtime dependency on the package being present. This suite's own tests below still use
/// MySqlConnector purely as the generic library-install mechanism's example package, independent of
/// that one driver project.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibraryLoadTests : IAsyncLifetime
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-library-test-{Guid.NewGuid():N}");

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
    public void MySqlConnector_IsReferencedOnlyByItsOwnBuiltInDriver_WithRuntimeAssetsExcluded()
    {
        var repoRoot = FindRepoRoot();
        var csprojFiles = Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        // A real <PackageReference Include="MySqlConnector" ...>, not a bare substring — MySqlConnector
        // is this whole test suite's own canary example of "restored, only ever compiled against by its
        // one built-in driver (and that driver's own test project)" (phase 109c, narrowed by phase 147),
        // so a comment explaining that pattern elsewhere is expected to name it too, the same way
        // DbDataSync.State.Tests.csproj's own comment on LibraryInstallFixture does.
        //
        // Exactly two legitimate references, the same split every other built-in driver's package has
        // (e.g. Microsoft.Data.SqlClient in DbDataSync.Drivers.MsSql.csproj vs. DbDataSync.Api.Tests.csproj):
        // the driver itself, ExcludeAssets="runtime" so the assembly still isn't physically shipped by it
        // (loadable only through an installed library's armed resolver — BuiltInDriverLibraries, phase
        // 144); and that driver's own test project, which needs the real runtime assembly directly to
        // exercise it and carries no such exclusion.
        var referencingProjects = new List<string>();
        foreach (var path in csprojFiles)
        {
            var content = File.ReadAllText(path);
            if (!content.Contains("Include=\"MySqlConnector\"", StringComparison.Ordinal))
                continue;

            var fileName = Path.GetFileName(path);
            referencingProjects.Add(fileName);

            if (fileName == "DbDataSync.Drivers.MySql.csproj")
                Assert.Contains("ExcludeAssets=\"runtime\"", content, StringComparison.Ordinal);
            else
                Assert.Equal("DbDataSync.Drivers.MySql.Tests.csproj", fileName);
        }

        Assert.Equal(
            new HashSet<string> { "DbDataSync.Drivers.MySql.csproj", "DbDataSync.Drivers.MySql.Tests.csproj" },
            referencingProjects.ToHashSet());
    }

    [Fact]
    public async Task Install_RestoresTheClosure_AndWritesTheManifest()
    {
        var manifest = await LibraryInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");

        Assert.Equal("MySqlConnector", manifest.Id);
        var libraryDir = LibraryPaths.LibraryDir(_repoRoot, "MySqlConnector");
        Assert.True(File.Exists(LibraryPaths.ManifestPath(libraryDir)));

        var libDir = LibraryPaths.LibDir(libraryDir);
        var dlls = Directory.EnumerateFiles(libDir, "*.dll").Select(Path.GetFileName).ToList();
        Assert.Contains("MySqlConnector.dll", dlls);
        Assert.True(Directory.EnumerateFiles(libDir, "*.deps.json").Any());
    }

    [Fact]
    public async Task GetFactory_OpensARealConnectionToMySql_WithNoCompileTimeReference()
    {
        await LibraryInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");

        var registry = new LibraryRegistry(_repoRoot).LoadAll();
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

    /// <summary>
    /// Phase 122's acceptance criterion, at the layer that actually owns it: <see cref="LibraryInstaller"/>
    /// itself never consults <see cref="KnownLibraries"/> — only its callers (the CLI, the API) do — so
    /// calling it directly with <c>factoryType: null</c> is exactly "MySqlConnector, as if it were not in
    /// the catalog". Reflection-assist finds it, and the resulting factory really opens the container.
    /// </summary>
    [Fact]
    public async Task Install_WithoutAFactoryType_DiscoversItByReflection_AndTheFactoryResolves()
    {
        var manifest = await LibraryInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")], factoryType: null);

        Assert.Equal("MySqlConnector.MySqlConnectorFactory, MySqlConnector", manifest.FactoryType);

        var registry = new LibraryRegistry(_repoRoot).LoadAll();
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

    /// <summary>A real, restorable package with no <see cref="DbProviderFactory"/> subclass at all —
    /// proves the "zero matches" branch of reflection-assist ends in the same clear failure a caller
    /// gets today, and that nothing is left half-installed on disk afterward.</summary>
    [Fact]
    public async Task Install_WithoutAFactoryType_WhenNothingIsFound_FailsAndLeavesNothingInstalled()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryInstaller.InstallAsync(
            _repoRoot, "Newtonsoft.Json", [new PackageRef("Newtonsoft.Json", "13.0.3")], factoryType: null));

        Assert.Contains("DbProviderFactory", ex.Message);
        Assert.False(Directory.Exists(LibraryPaths.LibraryDir(_repoRoot, "Newtonsoft.Json")));
    }

    [Fact]
    public void GetFactory_ForAnUninstalledLibrary_NamesTheInstallCommand()
    {
        var registry = new LibraryRegistry(_repoRoot).LoadAll();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetFactory("NotInstalled"));

        Assert.Contains("dbdatasync config library install", ex.Message);
    }

    /// <summary>
    /// The scenario `follow-up-library-install-paths-disagree-on-the-resulting-library-id.md` documented:
    /// installed via `POST /api/libraries` (keyed by package id, <c>"MySqlConnector"</c>), then looked up
    /// by the catalog id (<c>"mysql-connector"</c>) a driver.yaml's <c>library:</c> field or
    /// <c>from-catalog</c> would use instead. Names the installed id directly as a "did you mean" — not
    /// the generic "not installed".
    /// </summary>
    [Fact]
    public async Task GetFactory_ForACatalogId_WhenOnlyThePackageIdIsInstalled_NamesTheInstalledPackageId()
    {
        await LibraryInstaller.InstallAsync(
            _repoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");
        var registry = new LibraryRegistry(_repoRoot).LoadAll();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetFactory("mysql-connector"));

        Assert.Contains("under the package id 'MySqlConnector'", ex.Message);
        Assert.Contains("Did you mean 'MySqlConnector'", ex.Message);
    }

    /// <summary>The reverse direction: installed via the catalog id (<c>POST /api/drivers/from-catalog</c>'s
    /// own shape), looked up by the raw package id instead.</summary>
    [Fact]
    public async Task GetFactory_ForAPackageId_WhenOnlyTheCatalogIdIsInstalled_NamesTheInstalledCatalogId()
    {
        await LibraryInstaller.InstallAsync(
            _repoRoot, "mysql-connector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector");
        var registry = new LibraryRegistry(_repoRoot).LoadAll();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetFactory("MySqlConnector"));

        Assert.Contains("under the catalog id 'mysql-connector'", ex.Message);
        Assert.Contains("Did you mean 'mysql-connector'", ex.Message);
    }

    /// <summary>An id nobody's installed under either shape still gets the plain, original message — the
    /// alias hint only fires when there is a real alias to name.</summary>
    [Fact]
    public async Task GetFactory_ForACatalogId_WhenNeitherShapeIsInstalled_NamesTheInstallCommand_NotAPhantomAlias()
    {
        await LibraryInstaller.InstallAsync(
            _repoRoot, "Npgsql", [new PackageRef("Npgsql", "9.0.3")],
            "Npgsql.NpgsqlFactory, Npgsql");
        var registry = new LibraryRegistry(_repoRoot).LoadAll();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetFactory("mysql-connector"));

        Assert.Contains("dbdatasync config library install mysql-connector", ex.Message);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
