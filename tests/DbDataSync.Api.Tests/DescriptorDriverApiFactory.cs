using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="TestApiFactory"/> whose repo root already has <c>MySqlConnector</c> restored and a
/// <c>mysql.generic</c> descriptor written, before the host ever starts — exactly the state an
/// operator would leave a repo in after <c>dbdatasync config driver install mysql.generic --library
/// MySqlConnector --version 2.4.0 --from mysql</c>. What <see cref="DescriptorDriverTests"/> proves is
/// that the API (and, per the "two composition roots" note, the TaskRunner it spawns) picks this up
/// with no rebuild.
/// </summary>
public sealed class DescriptorDriverApiFactory : TestApiFactory
{
    public const string DriverId = "mysql.generic";

    public DescriptorDriverApiFactory()
    {
        LibraryInstaller.InstallAsync(
            RepoRoot, "MySqlConnector", [new PackageRef("MySqlConnector", "2.4.0")],
            "MySqlConnector.MySqlConnectorFactory, MySqlConnector").GetAwaiter().GetResult();

        var driverDir = Path.Combine(RepoRoot, "drivers", DriverId);
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), DriverYaml);
    }

    // The plan doc's own worked example (§*Worked examples* → *The descriptor*), trimmed to the
    // columns this test's tables actually use. GenericConnectionStringKeys' SqlClient-shaped
    // defaults (Host/Port/Database/"User Id"/Password/"Connect Timeout") all happen to be accepted
    // aliases in MySqlConnector's own builder, so no connectionStringKeys override is needed here —
    // confirmed empirically, not assumed.
    private const string DriverYaml = """
        id: mysql.generic
        displayName: MySQL / MariaDB (generic)
        library: MySqlConnector
        dialect:
          quoteIdentifier: backtick
          parameterPrefix: "@"
          rowLimit: limitOffset
          catalog: informationSchema
          supportsChangeDatabase: true
          defaultDatabase: ""
        typeMap:
          int: Int32
          "varchar(n)": { kind: String, length: n, unicode: true }
          datetime: Timestamp
        capabilities:
          readers: [Watermark, BatchReload]
          staging: [StagingTable]
          writers: [DeleteInsert]
        """;
}
