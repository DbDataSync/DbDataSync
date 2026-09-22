using DbDataSync.Libraries;

namespace DbDataSync.Api.Tests;

/// <summary>An <see cref="AuthenticatedApiFactory"/> with <c>mysql-connector</c> installed but no
/// driver.yaml seeded — a clean slate for <see cref="DriverAuthoringTests"/>' own create/update tests,
/// which need a real, resolvable library id to build an ADO.NET driver against but want to name their
/// own driver ids without colliding with an already-seeded one.</summary>
public sealed class DriverAuthoringApiFactory : AuthenticatedApiFactory
{
    public const string LibraryId = "mysql-connector";

    public DriverAuthoringApiFactory()
    {
        LibraryInstaller.InstallAsync(
            RepoRoot, LibraryId, [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector").GetAwaiter().GetResult();
    }
}
