using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// Phase 130, tier 2 — <see cref="ManagedSelfSignedCertificate"/>'s three pure/file-system pieces:
/// generation (SANs, validity), storage (path, permissions, git-ignore), and the renewal decision. No
/// Windows dependency anywhere here — the whole point of this class.
/// </summary>
public sealed class ManagedSelfSignedCertificateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-managed-selfsigned-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void Generate_CarriesTheHostAndLocalhostAsSans_AndTheRequestedValidity()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 90);

        var dnsNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Contains("dbdatasync.example.com", dnsNames);
        Assert.Contains("localhost", dnsNames);

        var validity = certificate.NotAfter - certificate.NotBefore;
        Assert.InRange(validity.TotalDays, 89.9, 90.1);
    }

    [Fact]
    public void Generate_HostAlreadyLocalhost_DoesNotDuplicateTheSan()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("localhost", validityDays: 30);

        var dnsNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Single(dnsNames, name => string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Write_ProducesAFileLoadableWithNoPassword_AtTheWellKnownPath()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);

        var path = ManagedSelfSignedCertificate.PfxPath(_root);
        Assert.True(File.Exists(path));

        using var reloaded = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        Assert.Equal(certificate.Thumbprint, reloaded.Thumbprint);
    }

    [Fact]
    public void Write_OnNonWindows_RestrictsTheFileToTheOwner()
    {
        if (OperatingSystem.IsWindows())
            return; // 0600 is a Unix permission concept — nothing to assert on Windows.

        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        var mode = File.GetUnixFileMode(ManagedSelfSignedCertificate.PfxPath(_root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Write_AddsTlsToGitignore_OnceEvenAcrossMultipleWrites()
    {
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);
        ManagedSelfSignedCertificate.Write(_root, certificate);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Single(lines, line => line == "tls/");
    }

    [Fact]
    public void Write_PreservesAnAlreadyExistingGitignore()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "state.db\n");
        using var certificate = ManagedSelfSignedCertificate.Generate("dbdatasync.example.com", validityDays: 30);

        ManagedSelfSignedCertificate.Write(_root, certificate);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Contains("state.db", lines);
        Assert.Contains("tls/", lines);
    }

    [Fact]
    public void PfxPath_IsStableAndNormalized_ForTheSameRepoRoot()
    {
        var first = ManagedSelfSignedCertificate.PfxPath(_root);
        var second = ManagedSelfSignedCertificate.PfxPath(_root + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.EndsWith(Path.Combine("tls", "dbdatasync.pfx"), first);
    }

    [Theory]
    [InlineData(10, 30, true)] // exactly a third of a 30-day lifetime remaining — the boundary is inclusive
    [InlineData(11, 30, false)]
    [InlineData(1, 30, true)]
    public void ShouldRenew_TriggersAtOneThirdOfLifetimeRemaining(int daysRemaining, int totalDays, bool expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = now.AddDays(daysRemaining);
        var notBefore = notAfter.AddDays(-totalDays);

        Assert.Equal(expected, ManagedSelfSignedCertificate.ShouldRenew(notBefore, notAfter, now));
    }

    [Fact]
    public void ShouldRenew_WellOutsideTheThreshold_IsFalse()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notBefore = now.AddDays(-10);
        var notAfter = now.AddDays(300); // ~310-day cert, 300 remaining — nowhere near a third

        Assert.False(ManagedSelfSignedCertificate.ShouldRenew(notBefore, notAfter, now));
    }
}
