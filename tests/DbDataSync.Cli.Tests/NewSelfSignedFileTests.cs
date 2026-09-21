using System.Security.Cryptography.X509Certificates;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config cert new-self-signed</c>'s non-Windows branch (phase 130, tier 2) — driven
/// through the real <see cref="CertCommand"/> against a real temp repo, the same way
/// <see cref="CertUsePemTests"/> exercises <c>use-pem</c>/<c>use-pfx</c>.
/// <para>
/// <see cref="NonWindowsFactAttribute"/> throughout, since phase 140: on Windows <c>CertCommand.Run</c>
/// dispatches <c>new-self-signed</c> to the *store*-based phase 82 implementation instead, so these
/// asserted a managed PFX that the command they invoked never set out to write. Not a bug in the
/// file-based path, which is genuinely cross-platform and which <c>SelfSignedCertificateServiceTests</c>
/// exercises on whatever OS runs it — a dispatch with no Windows door to it. That Windows therefore
/// cannot reach tier 2 from the CLI at all, while <c>DbDataSyncHost</c> will happily run its renewal
/// service there, is a real product gap; phase 140 names it and deliberately does not close it.
/// </para>
/// </summary>
public sealed class NewSelfSignedFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-new-self-signed-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [NonWindowsFact]
    public void NewSelfSigned_WritesTheManagedPfxAndPointsKestrelAtIt_AndCommits()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://console.local:5080");

        var exitCode = CertCommand.Run(["new-self-signed", "--repo", _root, "--days", "90"]);

        Assert.Equal(0, exitCode);

        var expectedPath = ManagedSelfSignedCertificate.PfxPath(_root);
        Assert.True(File.Exists(expectedPath));

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(expectedPath, config["Kestrel:Certificates:Default:Path"]);
        Assert.Equal("true", config["Kestrel:Certificates:Default:AllowInvalid"]);
        Assert.False(config.ContainsKey("Kestrel:Certificates:Default:KeyPath"));

        using var repo = new Repository(_root);
        Assert.Contains(repo.Commits, c => c.MessageShort.StartsWith("Generate a self-signed certificate", StringComparison.Ordinal));
    }

    [NonWindowsFact]
    public void NewSelfSigned_SansCoverTheConsoleUrlHostAndLocalhost()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://dbdatasync.example.com:5443");

        CertCommand.Run(["new-self-signed", "--repo", _root]);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            ManagedSelfSignedCertificate.PfxPath(_root), password: null);
        var dnsNames = CertificateSanReader.GetDnsNames(certificate);

        Assert.Contains("dbdatasync.example.com", dnsNames);
        Assert.Contains("localhost", dnsNames);
    }

    [NonWindowsFact]
    public void NewSelfSigned_NoConsoleUrlConfigured_DefaultsToLocalhost()
    {
        ServeCommand.Prepare(_root);

        var exitCode = CertCommand.Run(["new-self-signed", "--repo", _root]);

        Assert.Equal(0, exitCode);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            ManagedSelfSignedCertificate.PfxPath(_root), password: null);
        Assert.Contains("localhost", CertificateSanReader.GetDnsNames(certificate));
    }

    [NonWindowsFact]
    public void NewSelfSigned_AfterAPriorUsePem_RemovesTheStaleKeyPath()
    {
        ServeCommand.Prepare(_root);
        using var pemCertificate = CertificateBuilder.CreateSelfSigned(
            new CertificateSpec("console.local", ["console.local"], ValidityDays: 90, FriendlyName: null));
        var certPath = Path.Combine(_root, "cert.pem");
        var keyPath = Path.Combine(_root, "key.pem");
        File.WriteAllText(certPath, pemCertificate.ExportCertificatePem());
        using (var rsa = pemCertificate.GetRSAPrivateKey())
            File.WriteAllText(keyPath, rsa!.ExportPkcs8PrivateKeyPem());
        CertCommand.Run(["use-pem", "--cert", certPath, "--key", keyPath, "--repo", _root]);
        Assert.True(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:KeyPath"));

        var exitCode = CertCommand.Run(["new-self-signed", "--repo", _root]);

        Assert.Equal(0, exitCode);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(ManagedSelfSignedCertificate.PfxPath(_root), config["Kestrel:Certificates:Default:Path"]);
        Assert.False(config.ContainsKey("Kestrel:Certificates:Default:KeyPath"));
    }

    [NonWindowsFact]
    public void Status_AfterNewSelfSigned_ReportsTheFileCertificate()
    {
        ServeCommand.Prepare(_root);

        CertCommand.Run(["new-self-signed", "--repo", _root]);
        var (exitCode, output) = RunCaptured(["status", "--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Bound certificate (file):", output);
    }

    private static (int ExitCode, string Output) RunCaptured(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = CertCommand.Run(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
