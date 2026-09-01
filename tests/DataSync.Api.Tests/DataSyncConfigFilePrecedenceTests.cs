using DataSync.Core.Config;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// <c>datasync.config.yaml</c> is wired into <see cref="DataSyncHost.Build"/> at the same precedence
/// slot <c>appsettings.json</c> occupies — behind environment variables and the command line, so
/// either can still override a file value for a one-off run. Phase 79's whole point is that ordering;
/// see <c>DataSyncHost.InsertConfigFile</c>.
/// <para>
/// Calls <see cref="DataSyncHost.Build"/> directly rather than going through
/// <c>WebApplicationFactory</c> — this is about what <c>IConfiguration</c> resolves to, not about
/// serving a request, and building (without running) the host is enough to prove it.
/// </para>
/// </summary>
public sealed class DataSyncConfigFilePrecedenceTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("datasync-config-precedence-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string[] BaseArgs(params string[] extra) =>
    [
        "--DataSync:RepoRoot", _repoRoot,
        "--DataSync:StateDbPath", Path.Combine(_repoRoot, "state.db"),
        "--DataSync:TaskRunnerDllPath", Path.Combine(_repoRoot, "DataSync.TaskRunner.dll"),
        "--DataSync:Auth:Disabled", "true",
        .. extra,
    ];

    [Fact]
    public void AValueSetOnlyInTheFile_IsRead()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://file-only/");

        using var app = DataSyncHost.Build(BaseArgs());

        Assert.Equal("http://file-only/", app.Configuration["DataSync:Url"]);
    }

    [Fact]
    public void ACommandLineArgument_OverridesTheFile()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://from-file/");

        using var app = DataSyncHost.Build(BaseArgs("--DataSync:Url", "http://from-cli/"));

        Assert.Equal("http://from-cli/", app.Configuration["DataSync:Url"]);
    }

    [Fact]
    public void AnEnvironmentVariable_OverridesTheFile()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://from-file/");

        Environment.SetEnvironmentVariable("DataSync__Url", "http://from-env/");
        try
        {
            using var app = DataSyncHost.Build(BaseArgs());
            Assert.Equal("http://from-env/", app.Configuration["DataSync:Url"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DataSync__Url", null);
        }
    }

    [Fact]
    public void NoConfigFile_TheHostStillBuilds_AndFallsBackToOrdinaryDefaults()
    {
        using var app = DataSyncHost.Build(BaseArgs());

        Assert.Null(app.Configuration["DataSync:Url"]);
    }
}
