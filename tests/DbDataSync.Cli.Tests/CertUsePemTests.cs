using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using LibGit2Sharp;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config cert use-pem</c>/<c>use-pfx</c> (phase 113) — the cross-platform TLS answer for
/// Linux/macOS, driven through the real <see cref="CertCommand"/> against a real temp repo, the same
/// way <see cref="ServeCommandPrepareTests"/> exercises <c>ServeCommand.Prepare</c>.
/// </summary>
public sealed class CertUsePemTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-cert-use-pem-tests-").FullName;
    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly string _pfxPath;
    private readonly X509Certificate2 _certificate;

    public CertUsePemTests()
    {
        _certificate = CertificateBuilder.CreateSelfSigned(
            new CertificateSpec("test.local", ["test.local"], ValidityDays: 90, FriendlyName: null));

        _certPath = Path.Combine(_root, "cert.pem");
        _keyPath = Path.Combine(_root, "key.pem");
        _pfxPath = Path.Combine(_root, "cert.pfx");

        File.WriteAllText(_certPath, _certificate.ExportCertificatePem());
        using (var rsa = _certificate.GetRSAPrivateKey())
            File.WriteAllText(_keyPath, rsa!.ExportPkcs8PrivateKeyPem());
        File.WriteAllBytes(_pfxPath, _certificate.Export(X509ContentType.Pfx));
    }

    public void Dispose()
    {
        _certificate.Dispose();
        GitTempDirectory.DeleteRecursively(_root);
    }

    [Fact]
    public void UsePem_ValidUnencryptedPair_WritesPathAndKeyPathAndCommits()
    {
        ServeCommand.Prepare(_root);

        var exitCode = CertCommand.Run(["use-pem", "--cert", _certPath, "--key", _keyPath, "--repo", _root]);

        Assert.Equal(0, exitCode);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(_certPath, config["Kestrel:Certificates:Default:Path"]);
        Assert.Equal(_keyPath, config["Kestrel:Certificates:Default:KeyPath"]);

        using var repo = new Repository(_root);
        Assert.Contains(repo.Commits, c => c.MessageShort.StartsWith("Use PEM certificate", StringComparison.Ordinal));
    }

    [Fact]
    public void UsePem_MissingCertFile_FailsAndWritesNothing()
    {
        ServeCommand.Prepare(_root);

        var exitCode = CertCommand.Run(
            ["use-pem", "--cert", Path.Combine(_root, "missing.pem"), "--key", _keyPath, "--repo", _root]);

        Assert.NotEqual(0, exitCode);
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:Path"));
    }

    [Fact]
    public void UsePem_EncryptedPrivateKey_FailsWithAClearMessageAndWritesNothing()
    {
        ServeCommand.Prepare(_root);
        var encryptedKeyPath = Path.Combine(_root, "key-encrypted.pem");
        using (var rsa = _certificate.GetRSAPrivateKey())
        {
            var pbeParameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
            File.WriteAllText(encryptedKeyPath, rsa!.ExportEncryptedPkcs8PrivateKeyPem("hunter2", pbeParameters));
        }

        var (exitCode, output) = RunCaptured(["use-pem", "--cert", _certPath, "--key", encryptedKeyPath, "--repo", _root]);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not supported yet", output);
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:Path"));
    }

    [Fact]
    public void UsePfx_ValidUnprotectedFile_WritesPathOnlyAndCommits()
    {
        ServeCommand.Prepare(_root);

        var exitCode = CertCommand.Run(["use-pfx", "--pfx", _pfxPath, "--repo", _root]);

        Assert.Equal(0, exitCode);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(_pfxPath, config["Kestrel:Certificates:Default:Path"]);
        Assert.False(config.ContainsKey("Kestrel:Certificates:Default:KeyPath"));

        using var repo = new Repository(_root);
        Assert.Contains(repo.Commits, c => c.MessageShort.StartsWith("Use PFX certificate", StringComparison.Ordinal));
    }

    [Fact]
    public void UsePfx_PasswordProtected_FailsWithAClearMessageAndWritesNothing()
    {
        ServeCommand.Prepare(_root);
        var protectedPfxPath = Path.Combine(_root, "protected.pfx");
        File.WriteAllBytes(protectedPfxPath, _certificate.Export(X509ContentType.Pfx, "hunter2"));

        var (exitCode, output) = RunCaptured(["use-pfx", "--pfx", protectedPfxPath, "--repo", _root]);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("not supported yet", output);
        Assert.False(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:Path"));
    }

    /// <summary>The correctness fix this phase needed: Kestrel's certificate loader treats the section
    /// as PEM-shaped whenever <c>KeyPath</c> is present, so switching to a PFX must remove a stale one
    /// left behind by an earlier <c>use-pem</c> — otherwise it would try to open the PFX file as a
    /// private key.</summary>
    [Fact]
    public void UsePfx_AfterAPriorUsePem_RemovesTheStaleKeyPath()
    {
        ServeCommand.Prepare(_root);
        CertCommand.Run(["use-pem", "--cert", _certPath, "--key", _keyPath, "--repo", _root]);
        Assert.True(DbDataSyncConfigFile.Read(_root).ContainsKey("Kestrel:Certificates:Default:KeyPath"));

        var exitCode = CertCommand.Run(["use-pfx", "--pfx", _pfxPath, "--repo", _root]);

        Assert.Equal(0, exitCode);
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(_pfxPath, config["Kestrel:Certificates:Default:Path"]);
        Assert.False(config.ContainsKey("Kestrel:Certificates:Default:KeyPath"));
    }

    [Fact]
    public void Status_AfterUsePem_ReportsTheFileCertificate()
    {
        ServeCommand.Prepare(_root);
        CertCommand.Run(["use-pem", "--cert", _certPath, "--key", _keyPath, "--repo", _root]);

        var (exitCode, output) = RunCaptured(["status", "--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Bound certificate (file):", output);
        Assert.Contains("test.local", output);
    }

    [Fact]
    public void Status_NothingConfigured_ReportsNoCertificateAndPointsAtUsePemUsePfx()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = RunCaptured(["status", "--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.Contains("use-pem", output);
        Assert.Contains("use-pfx", output);
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
