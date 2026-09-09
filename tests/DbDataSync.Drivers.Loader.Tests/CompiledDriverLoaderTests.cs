using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Providers;
using Xunit;

namespace DbDataSync.Drivers.Loader.Tests;

/// <summary>
/// Loads a real compiled <see cref="IDriver"/> plugin — <c>tests/fixtures/DbDataSync.Drivers.LoaderTestFixture</c>,
/// published and installed exactly the way <c>dbdatasync config driver install --kind compiled</c> would leave
/// it on disk — through <see cref="DriverLoader.LoadCompiledDrivers"/>, and proves the isolation
/// property that makes the whole mechanism safe: the plugin's <c>IDriver</c> is the *same* <c>Type</c>
/// as the host's, not a second incompatible one from a redundant copy of
/// <c>DbDataSync.Drivers.Abstractions</c> the plugin's own publish output happens to carry.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompiledDriverLoaderTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-loader-test-{Guid.NewGuid():N}");

    public CompiledDriverLoaderTests() => Directory.CreateDirectory(_repoRoot);

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private void InstallFixture(string id, string driverType)
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", id);
        var libDir = Path.Combine(driverDir, "lib");
        Directory.CreateDirectory(libDir);
        // Recursive, preserving subdirectories: AssemblyDependencyResolver expects a native asset
        // (Sqlite's e_sqlite3) at the same runtimes/<rid>/native/ path relative to the main assembly
        // that dotnet publish put it at, not flattened into lib/ itself.
        var sourceRoot = FixturePublisher.OutputDirectory;
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destPath = Path.Combine(libDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }

        new CompiledDriverManifest(
            id, CompiledDriverManifest.CompiledKind, "DbDataSync.Drivers.LoaderTestFixture.dll", driverType,
            [new ProviderPackageRef("DbDataSync.Drivers.LoaderTestFixture", "1.0.0")])
            .Write(Path.Combine(driverDir, CompiledDriverManifest.FileName));
    }

    [Fact]
    public async Task LoadCompiledDrivers_RegistersThePlugin_AndARoundTripSucceeds()
    {
        InstallFixture("fixture-driver", "DbDataSync.Drivers.LoaderTestFixture.FixtureDriver");
        var registry = new DriverRegistry();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry);

        Assert.True(registry.TryGet("fixture.loadertest", out var driver));
        Assert.NotNull(driver);

        await using var connection = driver!.CreateConnection(
            new ConnectionConfig
            {
                Name = "fixture", DriverType = "fixture.loadertest", AuthMode = AuthMode.None,
                ConnectionString = "Data Source=:memory:",
            },
            credential: null);

        var roundTrip = driver.GetType().GetMethod("RoundTripAsync")!;
        var task = (Task<long>)roundTrip.Invoke(driver, [connection, CancellationToken.None])!;
        Assert.Equal(1L, await task);
    }

    [Fact]
    public void LoadCompiledDrivers_IsolationHolds_ThePluginsIDriverIsTheHostsType()
    {
        InstallFixture("fixture-driver-identity", "DbDataSync.Drivers.LoaderTestFixture.FixtureDriver");
        var registry = new DriverRegistry();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry);

        Assert.True(registry.TryGet("fixture.loadertest", out var driver));
        // The assertion that matters: this project has its own DbDataSync.Drivers.Abstractions
        // reference (transitively, through DbDataSync.Drivers.Descriptor), and the fixture's own
        // publish output carries a *second* copy of that same assembly. If the loader's isolated
        // AssemblyLoadContext preferred its own private copy over the host's already-loaded one, this
        // `is` check would fail — two different Type objects named IDriver, neither assignable to the
        // other, which is the standard failure mode a naive plugin loader falls into.
        Assert.IsAssignableFrom<IDriver>(driver);
    }

    [Fact]
    public void LoadCompiledDrivers_AContractVersionTheHostCannotSupport_IsRefused_AndSkipped()
    {
        InstallFixture("fixture-bad-contract", "DbDataSync.Drivers.LoaderTestFixture.FixtureDriverBadContract");
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        Assert.False(registry.TryGet("fixture.badcontract", out _));
        var error = Assert.Single(errors);
        Assert.Contains("contract version 999", error.Exception.Message);
        Assert.Contains(DriverContract.CurrentVersion.ToString(), error.Exception.Message);
    }

    [Fact]
    public void LoadCompiledDrivers_AMissingAssembly_IsSkippedRatherThanThrowing()
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", "fixture-missing-lib");
        Directory.CreateDirectory(driverDir);
        new CompiledDriverManifest(
            "fixture-missing-lib", CompiledDriverManifest.CompiledKind, "DoesNotExist.dll", "Some.Type",
            [new ProviderPackageRef("DoesNotExist", "1.0.0")])
            .Write(Path.Combine(driverDir, CompiledDriverManifest.FileName));
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        Assert.Single(errors);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void LoadCompiledDrivers_ADriverTypeThatIsNotAnIDriver_IsSkippedRatherThanThrowing()
    {
        // A real type in the fixture assembly (so it loads fine) that just isn't an IDriver.
        InstallFixture("fixture-wrong-type", "DbDataSync.Drivers.LoaderTestFixture.FixtureDriver");
        var manifestPath = Path.Combine(_repoRoot, "drivers", "fixture-wrong-type", CompiledDriverManifest.FileName);
        var manifest = CompiledDriverManifest.Read(manifestPath);
        (manifest with { DriverType = "DbDataSync.Drivers.LoaderTestFixture.NotADriver" }).Write(manifestPath);
        var registry = new DriverRegistry();
        var errors = new List<(string Message, Exception Exception)>();

        DriverLoader.LoadCompiledDrivers(_repoRoot, registry, (message, ex) => errors.Add((message, ex)));

        Assert.Single(errors);
        Assert.Contains("does not implement IDriver", errors[0].Exception.Message);
    }
}
