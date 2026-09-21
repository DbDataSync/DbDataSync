using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Certificates;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 130, tier 2 — <see cref="SelfSignedCertificateService"/> driven directly through
/// <see cref="SelfSignedCertificateService.CheckAsync"/>, the same way
/// <see cref="CertificateExpiryServiceWindowsTests"/> drives <c>CertificateExpiryService</c>: a real
/// temp <c>tls/</c>, a real (SQLite) <see cref="NotificationStore"/>, no fake clock — the certificate's
/// own <c>NotBefore</c>/<c>NotAfter</c> are what's varied, the same way
/// <see cref="Certificates.Tests.ManagedSelfSignedCertificateTests"/> varies them for the pure
/// evaluator this service calls.
/// </summary>
public sealed class SelfSignedCertificateServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-selfsigned-service-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (SelfSignedCertificateService Service, NotificationStore Notifications) BuildService(string? url = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(url is null
                ? []
                : new Dictionary<string, string?> { ["DbDataSync:App:Url"] = url })
            .Build();

        var database = new StateDatabase(Path.Combine(_root, "state.db"));
        var notifications = new NotificationStore(database);
        var apiOptions = new ApiOptions
        {
            RepoRoot = _root,
            StateDbPath = Path.Combine(_root, "state.db"),
            TaskRunnerDllPath = "unused",
            CliDllPath = "unused",
        };

        var service = new SelfSignedCertificateService(
            apiOptions, configuration, notifications, NullLogger<SelfSignedCertificateService>.Instance);

        return (service, notifications);
    }

    private void WriteManagedCertificate(string host, int validityDays)
    {
        using var certificate = ManagedSelfSignedCertificate.Generate(host, validityDays);
        ManagedSelfSignedCertificate.Write(_root, certificate);
    }

    /// <summary>
    /// <see cref="ManagedSelfSignedCertificate.Generate"/> always anchors <c>NotBefore</c> a few
    /// minutes before "now" (absorbing clock skew — see <see cref="CertificateBuilder"/>), so a
    /// freshly-generated certificate is never actually near its own expiry regardless of how short its
    /// validity is. To exercise "inside the renewal window" for real, this builds a certificate with an
    /// explicit, arbitrary <paramref name="notBefore"/>/<paramref name="notAfter"/> directly — the same
    /// <see cref="CertificateRequest.CreateSelfSigned"/> call <see cref="CertificateBuilder"/> itself
    /// makes, just with the window under this test's control instead of pinned to "now".
    /// </summary>
    private void WriteAgedManagedCertificate(string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(host);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        ManagedSelfSignedCertificate.Write(_root, certificate);
    }

    [Fact]
    public async Task CheckAsync_NoManagedCertificateFile_DoesNothing()
    {
        var (service, notifications) = BuildService();

        await service.CheckAsync();

        Assert.Empty(notifications.List());
    }

    [Fact]
    public async Task CheckAsync_CertificateWellOutsideTheRenewalWindow_DoesNothing()
    {
        WriteManagedCertificate("dbdatasync.example.com", validityDays: 397);
        var originalPath = ManagedSelfSignedCertificate.PfxPath(_root);
        var originalBytes = File.ReadAllBytes(originalPath);

        var (service, notifications) = BuildService();
        await service.CheckAsync();

        Assert.Empty(notifications.List());
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
    }

    [Fact]
    public async Task CheckAsync_CertificateInsideTheRenewalWindow_RegeneratesAndRaisesExactlyOneNotification()
    {
        // A 90-day certificate, 89 days into its own lifetime — 1 day of its 90 remains, well inside
        // the one-third-remaining (30-day) renewal threshold.
        var now = DateTimeOffset.UtcNow;
        WriteAgedManagedCertificate("dbdatasync.example.com", now.AddDays(-89), now.AddDays(1));
        var path = ManagedSelfSignedCertificate.PfxPath(_root);
        var originalBytes = File.ReadAllBytes(path);

        var (service, notifications) = BuildService("https://dbdatasync.example.com:5443");
        await service.CheckAsync();

        Assert.NotEqual(originalBytes, File.ReadAllBytes(path));

        var notification = Assert.Single(notifications.List());
        Assert.Equal(NotificationKinds.CertificateExpiring, notification.Kind);
        Assert.Contains("renewed", notification.Message);
        Assert.Contains("Restart", notification.Message);
    }

    [Fact]
    public async Task CheckAsync_RegenerationFailure_LogsAndDoesNotThrow()
    {
        WriteManagedCertificate("dbdatasync.example.com", validityDays: 3);
        var path = ManagedSelfSignedCertificate.PfxPath(_root);

        // Corrupt the file in place: it still exists (so the service reaches the load), but no longer
        // loads as a certificate — CheckAsync must swallow this, not throw.
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);

        var (service, notifications) = BuildService();
        await service.CheckAsync();

        Assert.Empty(notifications.List());
    }

    [Fact]
    public async Task CheckAsync_NeverTouchesACertificateAtAnyOtherPath()
    {
        // A file phase 113's use-pem/use-pfx would point at, sitting right next to the managed one —
        // this service must never read or write it, only its own well-known path.
        var otherPath = Path.Combine(_root, "operator-supplied.pfx");
        using (var operatorCertificate = ManagedSelfSignedCertificate.Generate("other.example.com", validityDays: 3))
            File.WriteAllBytes(otherPath, operatorCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        var otherBytesBefore = File.ReadAllBytes(otherPath);

        // No managed certificate exists at all — the service has nothing of its own to act on.
        var (service, notifications) = BuildService();
        await service.CheckAsync();

        Assert.Equal(otherBytesBefore, File.ReadAllBytes(otherPath));
        Assert.Empty(notifications.List());
    }
}
