using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Drivers.Descriptor.Tests;

/// <summary>
/// A descriptor naming a library nobody installed must not take the whole host down with it — the
/// existing <c>onError</c>-log-and-skip contract <see cref="DriverLoader.LoadDescriptorDrivers"/> has
/// held since 109d, exercised here specifically against phase 116's <c>library:</c> reference (as
/// opposed to the old inline <c>provider:</c> block, which could never name a library that "wasn't
/// installed" since it always carried its own factory type).
/// </summary>
public sealed class DriverLoaderTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-driverloader-test-{Guid.NewGuid():N}");

    public DriverLoaderTests() => Directory.CreateDirectory(_repoRoot);

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private const string DescriptorYaml = """
        id: mysql.generic
        displayName: MySQL / MariaDB (generic)
        library: mysql-connector
        dialect:
          quoteIdentifier: backtick
          parameterPrefix: "@"
          rowLimit: limitOffset
          catalog: informationSchema
        capabilities:
          readers: [Watermark]
          staging: [StagingTable]
          writers: [DeleteInsert]
        """;

    [Fact]
    public void ADescriptorNamingAnUninstalledLibrary_IsLoggedAndSkipped_AndDoesNotThrow()
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", "mysql.generic");
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), DescriptorYaml);

        var libraryRegistry = new LibraryRegistry(_repoRoot).LoadAll(); // nothing installed
        var driverRegistry = new DriverRegistry();

        string? loggedMessage = null;
        Exception? loggedException = null;
        var onError = (string message, Exception ex) =>
        {
            loggedMessage = message;
            loggedException = ex;
        };

        DriverLoader.LoadDescriptorDrivers(_repoRoot, libraryRegistry, driverRegistry, onError);

        Assert.NotNull(loggedException);
        Assert.Contains("dbdatasync config library install", loggedException!.Message);
        Assert.NotNull(loggedMessage);
        Assert.False(driverRegistry.TryGet("mysql.generic", out _));
    }

    [Fact]
    public void ADescriptorNamingAnUninstalledLibrary_DoesNotThrow_HostStartupSurvives()
    {
        var driverDir = Path.Combine(_repoRoot, "drivers", "mysql.generic");
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, DriverLoader.DescriptorFileName), DescriptorYaml);

        var libraryRegistry = new LibraryRegistry(_repoRoot).LoadAll(); // nothing installed
        var driverRegistry = new DriverRegistry();

        var exception = Record.Exception(() => DriverLoader.LoadDescriptorDrivers(_repoRoot, libraryRegistry, driverRegistry));

        Assert.Null(exception); // the default onError (stderr) swallows it exactly as the real host would
    }
}
