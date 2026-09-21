using ClrKernel.Core.Secrets;
using DbDataSync.Cli.Tui;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Libraries;
using DbDataSync.State;

namespace DbDataSync.Cli.Tests.Tui;

/// <summary>
/// Exercises <see cref="SetupSteps"/> directly — no TUI, no <c>IPromptIo</c>, just the same
/// config-file/secret-store assertions <c>SetupCommandTests</c> makes against the console flow. This
/// is the plan's "sidestep the Terminal.Gui headless-testing risk for the bulk of the work" bet: these
/// methods have zero Terminal.Gui dependency, so there is nothing here that needs a driver at all.
/// </summary>
public sealed class SetupStepsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-setupsteps-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static Task<LibraryManifest> FailingInstallLibrary(
        string repoRoot, string id, IReadOnlyList<PackageRef> packages, string factoryType, string? source,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This test's engine choice should never reach an install call.");

    [Fact]
    public void ApplyNotesRenderer_Ticked_WritesRich()
    {
        SetupSteps.ApplyNotesRenderer(_root, rich: true);

        Assert.Equal("rich", DbDataSyncConfigFile.Read(_root)["DbDataSync:Notes:MarkdownRenderer"]);
    }

    [Fact]
    public void ApplyNotesRenderer_Unticked_AddsNothingWhenTheKeyWasNeverSet()
    {
        // A save on an install that never touched the setting must not add a line for a default it already has.
        SetupSteps.ApplyNotesRenderer(_root, rich: false);

        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("DbDataSync:Notes:MarkdownRenderer"));
    }

    [Fact]
    public void ApplyNotesRenderer_Unticked_TurnsItOffWhenTheFileHadItOn()
    {
        SetupSteps.ApplyNotesRenderer(_root, rich: true);

        SetupSteps.ApplyNotesRenderer(_root, rich: false);

        Assert.Equal("basic", DbDataSyncConfigFile.Read(_root)["DbDataSync:Notes:MarkdownRenderer"]);
    }

    [Fact]
    public async Task ApplyStateDatabase_Sqlite_WritesNothingAndReportsPlainly()
    {
        var result = await SetupSteps.ApplyStateDatabaseAsync(
            _root, StateEngineIds.Sqlite, connectionString: null, password: null, FailingInstallLibrary);

        Assert.False(result.Warning);
        Assert.Equal("Using SQLite — nothing else to configure.", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.False(config.ContainsKey("DbDataSync:State:Engine"));
    }

    /// <summary>
    /// Phase 109h: choosing MsSql now installs <c>microsoft-data-sqlclient</c> first if it isn't
    /// already — a real install (<see cref="LibraryInstaller.InstallAsync"/>, the same delegate
    /// production wires through <c>SetupCommand.RunAsync</c>), not a fake, matching this repo's own
    /// "real, not mocked" precedent for anything that must actually make
    /// <see cref="StateDatabase.FromOptions"/> reach a real <c>DbProviderFactory</c>
    /// (<c>DbDataSync.State.Tests</c>' <c>LibraryInstallFixture</c>). Still uses a connection string
    /// nothing listens on, for a fast failure — only the pre-connect step (the library install) is now
    /// real network/disk I/O.
    /// </summary>
    [Fact]
    public async Task ApplyStateDatabase_MsSql_InstallsTheLibraryThenWritesConfigAndSecretAndReportsTheFailedConnection()
    {
        // Loopback with a port nothing listens on — a fast connection-refused rather than a real
        // server's DNS-timeout-length wait, same trick SetupCommandTests uses for this case.
        const string connectionString = "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;";

        var result = await SetupSteps.ApplyStateDatabaseAsync(
            _root, StateEngineIds.MsSql, connectionString, "hunter2", LibraryInstaller.InstallAsync);

        Assert.True(result.Warning);
        Assert.Contains("Could not connect yet", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(StateEngineIds.MsSql, config["DbDataSync:State:Engine"]);
        Assert.Equal(connectionString, config["DbDataSync:State:ConnectionString"]);

        var secrets = new SecretStore("DbDataSync", true);
        Assert.True(secrets.TryResolve(SecretRefs.ForAppSetting("stateConnectionString"), out var password));
        Assert.Equal("hunter2", password);
        secrets.Delete(SecretRefs.ForAppSetting("stateConnectionString"));

        Assert.True(new LibraryRegistry(_root).LoadAll().Installed.ContainsKey(MsSqlStateDialect.LibraryId));
    }

    /// <summary>The install-if-missing check is idempotent — a second call against a root that already
    /// has the library does not attempt to reinstall it (asserted by handing it an installLibrary that
    /// throws if ever invoked).</summary>
    [Fact]
    public async Task ApplyStateDatabase_MsSql_WhenLibraryAlreadyInstalled_DoesNotReinstall()
    {
        var entry = KnownLibraries.TryGetById(MsSqlStateDialect.LibraryId)!;
        await LibraryInstaller.InstallAsync(
            _root, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType);

        const string connectionString = "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;";
        var result = await SetupSteps.ApplyStateDatabaseAsync(
            _root, StateEngineIds.MsSql, connectionString, "hunter2", FailingInstallLibrary);

        Assert.True(result.Warning);
        Assert.Contains("Could not connect yet", result.Message);

        var secrets = new SecretStore("DbDataSync", true);
        secrets.Delete(SecretRefs.ForAppSetting("stateConnectionString"));
    }

    [Fact]
    public void ApplyAuthentication_PasskeysWithDefaults_WritesRelyingPartyWithNoWarning()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "passkeys", relyingPartyId: null, url: "http://localhost:5080",
            adminGroup: null, viewerGroup: null, noAuthConfirmed: false);

        Assert.False(result.Warning);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("localhost", config["DbDataSync:Auth:Passkeys:RelyingPartyId"]);
        // Origins is no longer stored — App:Url's own origin is always implicitly trusted at runtime.
        Assert.False(config.ContainsKey("DbDataSync:Auth:Passkeys:Origins:0"));
    }

    [Fact]
    public void ApplyAuthentication_PasskeysWithAUrlAsTheRelyingPartyId_ReturnsThePasskeyOptionsWarning()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "passkeys", relyingPartyId: "https://example.com", url: "https://example.com",
            adminGroup: null, viewerGroup: null, noAuthConfirmed: false);

        Assert.True(result.Warning);
        Assert.Contains("looks like a URL", result.Message);
    }

    [Fact]
    public void ApplyAuthentication_Windows_WritesAdminAndOptionalViewerGroup()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "windows", relyingPartyId: null, url: null, adminGroup: "DbDataSync Admins",
            viewerGroup: "", noAuthConfirmed: false);

        Assert.False(result.Warning);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("DbDataSync Admins", config["DbDataSync:Auth:Windows:AdminGroup"]);
        Assert.False(config.ContainsKey("DbDataSync:Auth:Windows:ViewerGroup"));
    }

    [Fact]
    public void ApplyAuthentication_NoneConfirmed_DisablesAuth()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "none", relyingPartyId: null, url: null, adminGroup: null, viewerGroup: null,
            noAuthConfirmed: true);

        Assert.True(result.Warning);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("loopback", config["DbDataSync:Auth:Network:Admin"]);
        Assert.Equal("remote", config["DbDataSync:Auth:Network:Viewer"]);
    }

    [Fact]
    public void ApplyAuthentication_NoneNotConfirmed_FallsBackToLocalhostPasskeys()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "none", relyingPartyId: null, url: null, adminGroup: null, viewerGroup: null,
            noAuthConfirmed: false);

        Assert.True(result.Warning);
        Assert.Contains("falling back to passkeys", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("localhost", config["DbDataSync:Auth:Passkeys:RelyingPartyId"]);
        Assert.False(config.ContainsKey("DbDataSync:Auth:Network:Admin"));
    }

    /// <summary>Phase 130 — printed, not invoked, matching every other certificate step in this
    /// walk-through; the trust caveat has to be in the printed lines, not only in docs.</summary>
    [Fact]
    public void SelfSignedCertificateInstructions_NamesTheRepoAndTheTrustCaveat()
    {
        var lines = SetupSteps.SelfSignedCertificateInstructions(_root);

        Assert.Contains(lines, line => line.Contains("new-self-signed") && line.Contains(_root));
        Assert.Contains(lines, line => line.Contains("trust this certificate once"));
    }
}
