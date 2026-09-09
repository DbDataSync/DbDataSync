using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Providers;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Drives <c>dbdatasync setup</c> end to end against a real temp repo, exactly the way an operator's
/// own typing would — only <see cref="ProviderInstaller.InstallAsync"/> is faked, since the real one
/// shells out to <c>dotnet publish</c> and would make the driver-step tests a network call.
/// </summary>
public sealed class SetupCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-setup-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public async Task FreshRoot_SqliteAndPasskeysAndNoDrivers_WritesConfigAndCommitsAndDoesNotStart()
    {
        var io = new ScriptedPromptIo(
            [
                "",   // config folder — accept the default (the --repo value itself)
                "",   // reachable at a hostname other than localhost? -> no
                "",   // console URL -> default
                "",   // state database -> default (SQLite)
                "",   // additional drivers -> none
                "",   // authentication -> default (passkeys)
                "",   // relying party id -> default (localhost)
                "",   // register as a systemd service? -> no (Linux step)
                "",   // point Kestrel at a certificate file? -> no (non-Windows step)
                "n",  // start DbDataSync now? -> no
            ]);

        var exitCode = await SetupCommand.RunAsync(["--repo", _root], io, FailingInstallProvider);

        Assert.Equal(0, exitCode);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("http://localhost:5080", config["DbDataSync:Url"]);
        Assert.Equal("localhost", config["DbDataSync:Auth:Passkeys:RelyingPartyId"]);
        Assert.Equal("http://localhost:5080", config["DbDataSync:Auth:Passkeys:Origins:0"]);

        using var repo = new Repository(_root);
        Assert.True(repo.Commits.Count() >= 2); // the starter commit, and setup's own.
        Assert.Contains(repo.Commits, c => c.MessageShort == "dbdatasync setup");

        Assert.Contains(io.Written, line => line.Contains("Setup is complete."));
        Assert.Contains(io.Written, line => line.Contains("FIRST-RUN.txt"));
    }

    [Fact]
    public async Task ExistingSetup_GoesStraightToTheReviewScreenAndPrintsEffectiveConfiguration()
    {
        await SetupCommand.RunAsync(
            ["--repo", _root], ScriptFor(SqliteWalkthroughWithNoStart), FailingInstallProvider);

        var reviewIo = new ScriptedPromptIo(["1", "4"]); // print effective configuration, then exit
        var exitCode = await SetupCommand.RunAsync(["--repo", _root], reviewIo, FailingInstallProvider);

        Assert.Equal(0, exitCode);
        Assert.Contains(reviewIo.Written, line => line.Contains("DbDataSync:Url = http://localhost:5080"));
        // Nothing here can carry a password — SetValue refuses one outright — but this is still the
        // seam an env-var-supplied connection string would have to be redacted through.
        Assert.DoesNotContain(reviewIo.Written, line => line.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonInteractive_RefusesAndPointsAtConfigCheck()
    {
        var io = new NonInteractivePromptIo();

        var exitCode = await SetupCommand.RunAsync(["--repo", _root], io, FailingInstallProvider);

        Assert.Equal(1, exitCode);
        Assert.Contains(io.Written, line => line.Contains("dbdatasync config check"));
    }

    [Fact]
    public async Task ServerStateEngine_StoresTheSecretAndInstallsTheChosenDriverThroughTheFakeInstaller()
    {
        var calls = new List<(string RepoRoot, string Id, IReadOnlyList<ProviderPackageRef> Packages, string FactoryType)>();
        Task<ProviderManifest> FakeInstall(
            string repoRoot, string id, IReadOnlyList<ProviderPackageRef> packages, string factoryType,
            string? source, CancellationToken cancellationToken)
        {
            calls.Add((repoRoot, id, packages, factoryType));
            return Task.FromResult(new ProviderManifest(id, factoryType, packages));
        }

        var io = new ScriptedPromptIo(
            lines:
            [
                "",                                         // config folder — default
                "",                                         // reachable at a hostname? -> no
                "",                                         // console URL -> default
                "2",                                        // state database -> SQL Server
                // Loopback with a port nothing listens on — a fast connection-refused rather than a
                // real server's DNS-timeout-length wait, since this test only cares that setup stores
                // the value and reports a failure, not that it can reach a real SQL Server.
                "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;",
                "4",                                         // additional drivers -> MySQL / MariaDB
                "8.0.32",                                    // MySqlConnector version
                "",                                          // authentication -> default (passkeys)
                "",                                          // relying party id -> default (localhost)
                "",                                          // register as a systemd service? -> no (Linux step)
                "",                                          // point Kestrel at a certificate file? -> no (non-Windows step)
                "n",                                         // start DbDataSync now? -> no
            ],
            keys: "hunter2\r".Select(c => c == '\r' ? ScriptedPromptIo.Enter : ScriptedPromptIo.Char(c)));

        var exitCode = await SetupCommand.RunAsync(["--repo", _root], io, FakeInstall);

        Assert.Equal(0, exitCode);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("MsSql", config["DbDataSync:StateEngine"]);
        Assert.Equal(
            "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;",
            config["DbDataSync:StateConnectionString"]);

        var secrets = new SecretStore("DbDataSync", true);
        Assert.True(secrets.TryResolve(SecretRefs.ForAppSetting("stateConnectionString"), out var password));
        Assert.Equal("hunter2", password);

        var call = Assert.Single(calls);
        Assert.Equal("MySqlConnector", call.Id);
        Assert.Equal("8.0.32", Assert.Single(call.Packages).Version);
        Assert.True(File.Exists(Path.Combine(_root, "drivers", "mysql", "driver.yaml")));

        secrets.Delete(SecretRefs.ForAppSetting("stateConnectionString"));
    }

    private static readonly string?[] SqliteWalkthroughWithNoStart =
        ["", "", "", "", "", "", "", "", "", "n"];

    private static ScriptedPromptIo ScriptFor(string?[] lines) => new(lines);

    private static readonly Func<string, string, IReadOnlyList<ProviderPackageRef>, string, string?, CancellationToken, Task<ProviderManifest>>
        FailingInstallProvider = (_, _, _, _, _, _) =>
            throw new InvalidOperationException("This test's walkthrough should never need to install a provider.");
}
