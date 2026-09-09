using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Loader.Tests;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="TestApiFactory"/> whose repo root already has the compiled
/// <c>tests/fixtures/DbDataSync.Drivers.LoaderTestFixture</c> plugin installed under
/// <c>drivers/&lt;id&gt;/</c> before the host ever starts — exactly the layout
/// <c>dbdatasync config driver install --kind compiled</c> would leave. What
/// <see cref="LibrariesAndDriversEndpointTests"/> proves is that <c>GET /api/drivers</c> reports this
/// as <c>source: "compiled"</c> (phase 118), distinct from a built-in or a <c>driver.yaml</c>
/// descriptor.
/// </summary>
public sealed class CompiledDriverApiFactory : TestApiFactory
{
    public const string DriverId = "fixture.loadertest";

    public CompiledDriverApiFactory()
    {
        var driverDir = Path.Combine(RepoRoot, "drivers", DriverId);
        var libDir = Path.Combine(driverDir, "lib");
        Directory.CreateDirectory(libDir);

        var sourceRoot = FixturePublisher.OutputDirectory;
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destPath = Path.Combine(libDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }

        new CompiledDriverManifest(
            DriverId, CompiledDriverManifest.CompiledKind, "DbDataSync.Drivers.LoaderTestFixture.dll",
            "DbDataSync.Drivers.LoaderTestFixture.FixtureDriver",
            [new PackageRef("DbDataSync.Drivers.LoaderTestFixture", "1.0.0")])
            .Write(Path.Combine(driverDir, CompiledDriverManifest.FileName));
    }
}
