using System.Security.Cryptography.X509Certificates;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config cert new-self-signed</c>'s non-Windows branch (phase 130, tier 2) — driven
/// through the real <see cref="CertCommand"/> against a real temp repo, the same way
/// <see cref="CertUsePemTests"/> exercises <c>use-pem</c>/<c>use-pfx</c>.
/// </summary>
public sealed class NewSelfSignedFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-new-self-signed-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void NewSelfSigned_WritesTheManagedPfxAndPointsKestrelAtIt_AndCommits()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://console.local:5080");

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

    [Fact]
    public void NewSelfSigned_SansCoverTheConsoleUrlHostAndLocalhost()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync", "Url", "https://dbdatasync.example.com:5443");

        CertCommand.Run(["new-self-signed", "--repo", _root]);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            ManagedSelfSignedCertificate.PfxPath(_root), password: null);
        var dnsNames = CertificateSanReader.GetDnsNames(certificate);

        Assert.Contains("dbdatasync.example.com", dnsNames);
        Assert.Contains("localhost", dnsNames);
    }

    [Fact]
    public void NewSelfSigned_NoConsoleUrlConfigured_DefaultsToLocalhost()
    {
        ServeCommand.Prepare(_root);

        var exitCode = CertCommand.Run(["new-self-signed", "--repo", _root]);

        Assert.Equal(0, exitCode);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            ManagedSelfSignedCertificate.PfxPath(_root), password: null);
        Assert.Contains("localhost", CertificateSanReader.GetDnsNames(certificate));
    }

    [Fact]
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

    [Fact]
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
