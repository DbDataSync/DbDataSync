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

    /// <summary>A second driver against the same installed library, naming it by its real package id
    /// ("MySqlConnector") rather than the catalog id <see cref="DriverId"/>'s own descriptor uses — the
    /// same catalog-id/package-id divergence <c>KnownLibraries.TryGetByIdOrPackageId</c> exists for,
    /// exercised here from the other direction (a driver.yaml naming the package id while the library is
    /// installed under the catalog id, rather than the reverse). Proves <c>LibrariesService</c>'s "used
    /// by" computation resolves either shape, not just the one both sides happened to agree on before.</summary>
    public const string DriverIdByPackageId = "mysql.generic.by-package-id";

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

        var byPackageIdDir = Path.Combine(RepoRoot, "drivers", DriverIdByPackageId);
        Directory.CreateDirectory(byPackageIdDir);
        var byPackageIdYaml = KnownDrivers.Render(
            knownDriver, DriverIdByPackageId, "MySQL / MariaDB (generic, by package id)", "MySqlConnector");
        File.WriteAllText(Path.Combine(byPackageIdDir, DriverLoader.DescriptorFileName), byPackageIdYaml);
    }
}
