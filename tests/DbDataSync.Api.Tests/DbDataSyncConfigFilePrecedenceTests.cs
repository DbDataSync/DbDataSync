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
        "--DbDataSync:App:RepoRoot", _repoRoot,
        "--DbDataSync:State:DbPath", Path.Combine(_repoRoot, "state.db"),
        "--DbDataSync:App:TaskRunnerDllPath", Path.Combine(_repoRoot, "DbDataSync.TaskRunner.dll"),
        "--DbDataSync:Auth:Network:Admin", "loopback",
        .. extra,
    ];

    [Fact]
    public void AValueSetOnlyInTheFile_IsRead()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync:App", "Url", "http://file-only/");

        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Equal("http://file-only/", app.Configuration["DbDataSync:App:Url"]);
    }

    [Fact]
    public void ACommandLineArgument_OverridesTheFile()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync:App", "Url", "http://from-file/");

        using var app = DbDataSyncHost.Build(BaseArgs("--DbDataSync:App:Url", "http://from-cli/"));

        Assert.Equal("http://from-cli/", app.Configuration["DbDataSync:App:Url"]);
    }

    [Fact]
    public void AnEnvironmentVariable_OverridesTheFile()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync:App", "Url", "http://from-file/");

        Environment.SetEnvironmentVariable("DbDataSync__App__Url", "http://from-env/");
        try
        {
            using var app = DbDataSyncHost.Build(BaseArgs());
            Assert.Equal("http://from-env/", app.Configuration["DbDataSync:App:Url"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__App__Url", null);
        }
    }

    [Fact]
    public void NoConfigFile_TheHostStillBuilds_AndFallsBackToOrdinaryDefaults()
    {
        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Null(app.Configuration["DbDataSync:App:Url"]);
    }
}
