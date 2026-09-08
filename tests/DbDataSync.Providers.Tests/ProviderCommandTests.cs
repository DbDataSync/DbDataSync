using DbDataSync.Cli;
using Xunit;

namespace DbDataSync.Providers.Tests;

/// <summary>Drives <c>dbdatasync provider ...</c> exactly as an operator would, through
/// <see cref="ProviderCommand.RunAsync"/> rather than the lower-level <see cref="ProviderInstaller"/>
/// this exercises indirectly.</summary>
[Trait("Category", "Integration")]
public sealed class ProviderCommandTests : IAsyncLifetime
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"dbdatasync-provider-cmd-test-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_repoRoot);
        Console.SetOut(_output);
        Console.SetError(_error);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args) =>
        ProviderCommand.RunAsync([.. args, "--repo", _repoRoot]);

    [Fact]
    public async Task InstallThenList_ReportsTheInstalledProvider()
    {
        Assert.Equal(0, await RunAsync("install", "MySqlConnector", "--version", "2.4.0"));

        _output.GetStringBuilder().Clear();
        Assert.Equal(0, await RunAsync("list"));

        Assert.Contains("MySqlConnector", _output.ToString());
        Assert.Contains("resolves", _output.ToString());
    }

    [Fact]
    public async Task Uninstall_RemovesTheProviderDirectory()
    {
        await RunAsync("install", "MySqlConnector", "--version", "2.4.0");
        var providerDir = ProviderPaths.ProviderDir(_repoRoot, "MySqlConnector");
        Assert.True(Directory.Exists(providerDir));

        Assert.Equal(0, await RunAsync("uninstall", "MySqlConnector"));

        Assert.False(Directory.Exists(providerDir));
    }

    [Fact]
    public async Task Sync_RebuildsLibFromTheManifestAlone()
    {
        await RunAsync("install", "MySqlConnector", "--version", "2.4.0");
        var libDir = ProviderPaths.LibDir(ProviderPaths.ProviderDir(_repoRoot, "MySqlConnector"));
        Directory.Delete(libDir, recursive: true);
        Assert.False(Directory.Exists(libDir));

        Assert.Equal(0, await RunAsync("sync", "MySqlConnector"));

        Assert.True(File.Exists(Path.Combine(libDir, "MySqlConnector.dll")));
    }

    [Fact]
    public async Task Install_WithoutAKnownFactoryType_RequiresOneExplicitly()
    {
        var exitCode = await RunAsync("install", "SomeUnknownPackage", "--version", "1.0.0");

        Assert.Equal(1, exitCode);
        Assert.Contains("--factory-type", _error.ToString());
    }
}
