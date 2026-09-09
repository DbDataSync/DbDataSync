namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config driver install --from &lt;knownDriverId&gt;</c> — the phase 117 catalog path,
/// exercised through <see cref="DriverCommand.RunAsync"/> exactly as an operator would type it. These
/// do a real <c>dotnet publish</c> against the public NuGet feed (installing the bound library), so
/// they're integration tests, not unit tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DriverCommandTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-drivercmd-test-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public DriverCommandTests()
    {
        Directory.CreateDirectory(_repoRoot);
        Console.SetOut(_output);
        Console.SetError(_error);
    }

    public void Dispose()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<int> RunAsync(params string[] args) => DriverCommand.RunAsync([.. args, "--repo", _repoRoot]);

    [Fact]
    public async Task InstallFromCatalog_WritesADescriptorMatchingTheGoldenBody_AndInstallsItsBoundLibrary()
    {
        Assert.Equal(0, await RunAsync("install", "my.mysql", "--version", "2.4.0", "--from", "mysql.generic"));

        var yamlPath = Path.Combine(_repoRoot, "drivers", "my.mysql", "driver.yaml");
        var text = (await File.ReadAllTextAsync(yamlPath)).Replace("\r\n", "\n");
        var lines = text.Split('\n');

        // Byte-identical to the golden file modulo the id/displayName/library header lines.
        Assert.Equal("id: my.mysql", lines[0]);
        Assert.Equal("displayName: my.mysql", lines[1]);
        Assert.Equal("library: mysql-connector", lines[2]);

        var body = string.Join('\n', lines.Skip(3));
        var goldenPath = Path.Combine(FindRepoRoot(), "tests", "DbDataSync.Cli.Tests", "Golden", "mysql.generic.driver.yaml.body");
        var golden = (await File.ReadAllTextAsync(goldenPath)).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(golden, body.TrimEnd('\n'));

        Assert.True(Directory.Exists(Path.Combine(_repoRoot, "libraries", "mysql-connector")));
    }

    [Fact]
    public async Task UnknownFromName_ListsTheCatalogIds()
    {
        var exitCode = await RunAsync("install", "whatever", "--version", "1.0.0", "--from", "bogus");

        Assert.Equal(1, exitCode);
        Assert.Contains("mysql.generic", _error.ToString());
    }

    [Fact]
    public async Task InstallFromCatalogTwice_ReusesTheAlreadyInstalledLibrary()
    {
        Assert.Equal(0, await RunAsync("install", "d1", "--version", "2.4.0", "--from", "mysql.generic"));
        _output.GetStringBuilder().Clear();

        Assert.Equal(0, await RunAsync("install", "d2", "--library", "mysql-connector", "--version", "2.4.0"));

        Assert.Contains("Reusing already-installed library 'mysql-connector'", _output.ToString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (DbDataSync.slnx).");
    }
}
