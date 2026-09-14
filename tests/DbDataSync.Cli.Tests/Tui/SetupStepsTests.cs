using ClrKernel.Core.Secrets;
using DbDataSync.Cli.Tui;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
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

    [Fact]
    public void ApplyStateDatabase_Sqlite_WritesNothingAndReportsPlainly()
    {
        var result = SetupSteps.ApplyStateDatabase(_root, StateEngineIds.Sqlite, connectionString: null, password: null);

        Assert.False(result.Warning);
        Assert.Equal("Using SQLite — nothing else to configure.", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.False(config.ContainsKey("DbDataSync:StateEngine"));
    }

    [Fact]
    public void ApplyStateDatabase_MsSql_WritesConfigAndSecretAndReportsTheFailedConnection()
    {
        // Loopback with a port nothing listens on — a fast connection-refused rather than a real
        // server's DNS-timeout-length wait, same trick SetupCommandTests uses for this case.
        const string connectionString = "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;";

        var result = SetupSteps.ApplyStateDatabase(_root, StateEngineIds.MsSql, connectionString, "hunter2");

        Assert.True(result.Warning);
        Assert.Contains("Could not connect yet", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(StateEngineIds.MsSql, config["DbDataSync:StateEngine"]);
        Assert.Equal(connectionString, config["DbDataSync:StateConnectionString"]);

        var secrets = new SecretStore("DbDataSync", true);
        Assert.True(secrets.TryResolve(SecretRefs.ForAppSetting("stateConnectionString"), out var password));
        Assert.Equal("hunter2", password);
        secrets.Delete(SecretRefs.ForAppSetting("stateConnectionString"));
    }

    [Fact]
    public void ApplyAuthentication_PasskeysWithDefaults_WritesRelyingPartyAndOriginWithNoWarning()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "passkeys", relyingPartyId: null, url: "http://localhost:5080",
            adminGroup: null, viewerGroup: null, noAuthConfirmed: false);

        Assert.False(result.Warning);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("localhost", config["DbDataSync:Auth:Passkeys:RelyingPartyId"]);
        Assert.Equal("http://localhost:5080", config["DbDataSync:Auth:Passkeys:Origins:0"]);
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
        Assert.Equal("DbDataSync Admins", config["DbDataSync:Auth:AdminGroup"]);
        Assert.False(config.ContainsKey("DbDataSync:Auth:ViewerGroup"));
    }

    [Fact]
    public void ApplyAuthentication_NoneConfirmed_DisablesAuth()
    {
        var result = SetupSteps.ApplyAuthentication(
            _root, "none", relyingPartyId: null, url: null, adminGroup: null, viewerGroup: null,
            noAuthConfirmed: true);

        Assert.True(result.Warning);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("true", config["DbDataSync:Auth:Disabled"]);
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
        Assert.False(config.ContainsKey("DbDataSync:Auth:Disabled"));
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
