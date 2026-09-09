using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Tests;

/// <summary>
/// An <see cref="AuthenticatedApiFactory"/> whose repo root already has a library and a matching
/// <c>driver.yaml</c> descriptor on disk before the host starts — what
/// <see cref="LibrariesControllerTests"/> and the drivers-endpoint additions (phase 118) exercise
/// <c>GET /api/libraries</c> and <c>GET /api/drivers</c> against.
/// </summary>
public sealed class LibrariesAdminApiFactory : AuthenticatedApiFactory
{
    public const string DriverId = "mysql.generic";
    public const string LibraryId = "mysql-connector";

    public LibrariesAdminApiFactory()
    {
        LibraryInstaller.InstallAsync(
            RepoRoot, LibraryId, [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector").GetAwaiter().GetResult();

        var driverDir = Path.Combine(RepoRoot, "drivers", DriverId);
        Directory.CreateDirectory(driverDir);
        var knownDriver = KnownDrivers.TryGetById(DriverId)!;
        var yaml = KnownDrivers.Render(knownDriver, DriverId, "MySQL / MariaDB (generic)", LibraryId);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), yaml);
    }
}
