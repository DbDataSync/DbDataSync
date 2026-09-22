namespace DbDataSync.Api.Tests;

/// <summary>
/// An <see cref="AuthenticatedApiFactory"/> with one JDBC-backed <c>driver.yaml</c> already on disk,
/// naming a file that doesn't exist yet — what <see cref="FilesControllerTests"/>' <c>usedBy</c> test
/// uploads into place. The jar itself is never real (no IKVM/live-jar dependency needed here):
/// <c>DriverDescriptorScanner.Scan</c> only parses the YAML for display, and
/// <c>DriverLoader.LoadDescriptorDrivers</c>'s own contract already tolerates a driver that fails to
/// actually load at host startup (logged and skipped, not fatal) — this test is about the <c>files/</c>
/// API surface, not about a working JDBC connection.
/// </summary>
public sealed class FilesApiFactory : AuthenticatedApiFactory
{
    public const string DriverId = "jdbc-files-test";
    public const string JarName = "pretend-driver.jar";

    public FilesApiFactory()
    {
        // No `base:` — DriverDescriptorScanner.Scan reads the jdbc block regardless of it (it's a
        // display-only static parse, not a build), and DriverLoader.LoadDescriptorDrivers' own catch
        // clause doesn't cover the FileNotFoundException a real JdbcGenericDriver construction throws
        // when "ikvm" isn't actually installed (a real, separate gap — not this fixture's to work around
        // by installing a library this test has no reason to need). Omitting `base:` builds this as a
        // plain GenericDriver instead, which fails with the InvalidOperationException LoadDescriptorDrivers
        // already handles cleanly (library "ikvm" doesn't resolve) — logged and skipped, not fatal.
        var driverDir = Path.Combine(RepoRoot, "drivers", DriverId);
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, "driver.yaml"), $$"""
            id: {{DriverId}}
            displayName: JDBC files test
            library: ikvm
            jdbc:
              driverClass: org.example.NotARealDriver
              driverJarPaths: [{{JarName}}]
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
            typeMap: {}
            capabilities:
              readers: []
              staging: []
              writers: []
            """);
    }
}
