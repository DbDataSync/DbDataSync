using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClrKernel.Core.Secrets;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config check</c>'s check list, run against real temp repos rather than mocked
/// contexts — the same reasoning <c>ServeCommandPrepareTests</c> uses. <see cref="BindingCheck"/> is
/// left out of every assertion here: none of these tests run a real DbDataSync host, so it always
/// reports "did not answer" regardless of the rest of the configuration, and asserting around it
/// would only be asserting that nothing is listening on port 5080.
/// </summary>
public sealed class ReadinessChecksTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-readiness-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public async Task FreshSqliteRepo_RepoAndStateStoreAndAuthChecksPass()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Repo").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "State store").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Providers / drivers").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Auth").Status);
    }

    [Fact]
    public async Task ConnectionNamingAnUninstalledDriver_ProvidersAndDriversCheckFails()
    {
        ServeCommand.Prepare(_root);
        var configRoot = Path.Combine(_root, "config");
        var repository = new ConfigRepository(
            configRoot, new GitCommitService(_root), new SecretStore("DbDataSync", true));
        repository.SaveConnection(
            new ConnectionInput
            {
                Name = "unreachable",
                DriverType = "NoSuchDriver",
                Host = "db.example",
                AuthMode = AuthMode.None,
            },
            CurrentUserForTests.Author);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Providers / drivers");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("dbdatasync config driver install", check.Detail);
    }

    [Fact]
    public async Task PasskeyRelyingPartyIdThatIsAUrl_AuthCheckFailsWithTheProblemText()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", "https://example.com");

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Auth");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("looks like a URL", check.Detail);
    }

    [Fact]
    public async Task NoFileBasedCertificateConfigured_CertificateCheckPasses()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Certificate").Status);
    }

    [Fact]
    public async Task ConfiguredCertificateFileMissing_CertificateCheckFails()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Path", Path.Combine(_root, "missing.pem"));
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "KeyPath", Path.Combine(_root, "missing-key.pem"));

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("does not exist", check.Detail);
    }

    [Fact]
    public async Task ConfiguredCertificate_SanCoversTheConsoleUrlHost_CertificateCheckPasses()
    {
        ServeCommand.Prepare(_root);
        var (certPath, keyPath) = WriteSelfSignedPem(_root, "console.local");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://console.local:5080");
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Path", certPath);
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "KeyPath", keyPath);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    [Fact]
    public async Task ConfiguredCertificate_SanDoesNotCoverTheConsoleUrlHost_CertificateCheckFails()
    {
        ServeCommand.Prepare(_root);
        var (certPath, keyPath) = WriteSelfSignedPem(_root, "console.local");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://a-different-host.example:5080");
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Path", certPath);
        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "KeyPath", keyPath);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("does not cover", check.Detail);
    }

    /// <summary>Writes a fresh self-signed PEM cert+key pair for <paramref name="dnsName"/>, valid 90
    /// days — comfortably outside the default 30-day expiry-warning window, so this is unambiguously
    /// an <see cref="CheckStatus.Ok"/> case rather than a <see cref="CheckStatus.Warn"/> one.</summary>
    private static (string CertPath, string KeyPath) WriteSelfSignedPem(string root, string dnsName)
    {
        using var certificate = CertificateBuilder.CreateSelfSigned(
            new CertificateSpec(dnsName, [dnsName], ValidityDays: 90, FriendlyName: null));

        var certPath = Path.Combine(root, "cert.pem");
        var keyPath = Path.Combine(root, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using (var rsa = certificate.GetRSAPrivateKey())
            File.WriteAllText(keyPath, rsa!.ExportPkcs8PrivateKeyPem());

        return (certPath, keyPath);
    }

    /// <summary>Drives the real CLI path (<c>dbdatasync config check --json</c>) rather than the
    /// engine directly, so this one test also proves <see cref="ConfigCommand"/>'s own dispatch and
    /// exit-code plumbing, not just the check list.</summary>
    [Fact]
    public async Task JsonOutput_IsValidAndNamesEveryCheck()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = RunConfigCheck(["check", "--repo", _root, "--json"]);

        Assert.Equal(1, exitCode); // BindingCheck fails — nothing is listening.
        using var document = JsonDocument.Parse(output);
        var names = document.RootElement.EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
        Assert.Contains("Repo", names);
        Assert.Contains("State store", names);
        Assert.Contains("Providers / drivers", names);
        Assert.Contains("Auth", names);
        Assert.Contains("Certificate", names);
        Assert.Contains("Binding", names);
        Assert.Contains("First admin", names);
    }

    private static CheckResult Find(IReadOnlyList<CheckResult> results, string name) =>
        results.Single(r => r.Name == name);

    private static (int ExitCode, string Output) RunConfigCheck(string[] args)
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = ConfigCommand.RunAsync(args).GetAwaiter().GetResult();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}

internal static class CurrentUserForTests
{
    public static GitAuthor Author { get; } = new("Test", "test@localhost");
}
