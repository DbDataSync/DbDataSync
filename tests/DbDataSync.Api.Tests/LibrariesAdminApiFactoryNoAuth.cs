using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Same on-disk seeding as <see cref="LibrariesAdminApiFactory"/> (a library and its matching
/// <c>driver.yaml</c> descriptor, written before the host starts), but on <see cref="TestApiFactory"/>
/// (authentication disabled) — for <c>GET /api/drivers</c> tests, which only need <c>Viewer</c> and so
/// don't need a signed-in client.
/// </summary>
public sealed class LibrariesAdminApiFactoryNoAuth : TestApiFactory
{
    public LibrariesAdminApiFactoryNoAuth()
    {
        LibraryInstaller.InstallAsync(
            RepoRoot, LibrariesAdminApiFactory.LibraryId, [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector").GetAwaiter().GetResult();

        var driverDir = Path.Combine(RepoRoot, "drivers", LibrariesAdminApiFactory.DriverId);
        Directory.CreateDirectory(driverDir);
        var knownDriver = KnownDrivers.TryGetById(LibrariesAdminApiFactory.DriverId)!;
        var yaml = KnownDrivers.Render(knownDriver, LibrariesAdminApiFactory.DriverId, "MySQL / MariaDB (generic)", LibrariesAdminApiFactory.LibraryId);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), yaml);
    }
}
