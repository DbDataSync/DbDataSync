using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <c>dbdatasync.config.yaml</c> is wired into <see cref="DbDataSyncHost.Build"/> at the same precedence
/// slot <c>appsettings.json</c> occupies — behind environment variables and the command line, so
/// either can still override a file value for a one-off run. Phase 79's whole point is that ordering;
/// see <c>DbDataSyncHost.InsertConfigFile</c>.
/// <para>
/// Calls <see cref="DbDataSyncHost.Build"/> directly rather than going through
/// <c>WebApplicationFactory</c> — this is about what <c>IConfiguration</c> resolves to, not about
/// serving a request, and building (without running) the host is enough to prove it.
/// </para>
/// </summary>
public sealed class DbDataSyncConfigFilePrecedenceTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-config-precedence-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string[] BaseArgs(params string[] extra) =>
    [
        "--DbDataSync:RepoRoot", _repoRoot,
        "--DbDataSync:StateDbPath", Path.Combine(_repoRoot, "state.db"),
        "--DbDataSync:TaskRunnerDllPath", Path.Combine(_repoRoot, "DbDataSync.TaskRunner.dll"),
        "--DbDataSync:Auth:Disabled", "true",
        .. extra,
    ];

    [Fact]
    public void AValueSetOnlyInTheFile_IsRead()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://file-only/");

        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Equal("http://file-only/", app.Configuration["DbDataSync:Url"]);
    }

    [Fact]
    public void ACommandLineArgument_OverridesTheFile()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://from-file/");

        using var app = DbDataSyncHost.Build(BaseArgs("--DbDataSync:Url", "http://from-cli/"));

        Assert.Equal("http://from-cli/", app.Configuration["DbDataSync:Url"]);
    }

    [Fact]
    public void AnEnvironmentVariable_OverridesTheFile()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://from-file/");

        Environment.SetEnvironmentVariable("DbDataSync__Url", "http://from-env/");
        try
        {
            using var app = DbDataSyncHost.Build(BaseArgs());
            Assert.Equal("http://from-env/", app.Configuration["DbDataSync:Url"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__Url", null);
        }
    }

    [Fact]
    public void NoConfigFile_TheHostStillBuilds_AndFallsBackToOrdinaryDefaults()
    {
        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Null(app.Configuration["DbDataSync:Url"]);
    }
}
