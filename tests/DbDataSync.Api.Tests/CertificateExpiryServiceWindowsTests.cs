using System.Runtime.Versioning;
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
/// End-to-end proof that <see cref="CertificateExpiryService"/>'s wiring — read
/// <c>Kestrel:Certificates:Default:*</c> off <see cref="IConfiguration"/>, find the certificate in the
/// store, evaluate it, raise through <see cref="NotificationStore"/> — actually connects, on top of a
/// real (CurrentUser-store, so no elevation needed) certificate. The individual pieces
/// (<see cref="CertificateExpiryEvaluator"/>, <see cref="NotificationStore.Raise"/>,
/// <see cref="CertificateStore"/>) each have their own focused tests; this is the one that would catch
/// a wiring mistake between them (a wrong section name, a wrong DI registration) that none of those
/// would.
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CertificateExpiryServiceWindowsTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-cert-expiry-tests-").FullName;
    private readonly List<X509Certificate2> _installed = [];

    public void Dispose()
    {
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in _installed)
            {
                if (certificate.GetRSAPrivateKey() is RSACng rsaCng)
                    rsaCng.Key.Delete();

                store.Remove(certificate);
                certificate.Dispose();
            }
        }

        Directory.Delete(_tempDir, recursive: true);
    }

    private X509Certificate2 InstallSelfSigned(string commonName)
    {
        var spec = new CertificateSpec(commonName, [commonName], 365, "DbDataSync Test");
        using var ephemeral = CertificateBuilder.CreateSelfSigned(spec);
        var certificate = CertificateStore.Install(ephemeral, StoreLocation.CurrentUser);
        _installed.Add(certificate);
        return certificate;
    }

    private (CertificateExpiryService Service, NotificationStore Notifications) BuildService(string subject, int expiryWarningDays)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Certificates:Default:Subject"] = subject,
                ["Kestrel:Certificates:Default:Store"] = "My",
                ["Kestrel:Certificates:Default:Location"] = "CurrentUser",
            })
            .Build();

        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        var notifications = new NotificationStore(database);
        var options = new CertificateOptions { ExpiryWarningDays = expiryWarningDays };

        var service = new CertificateExpiryService(
            configuration, options, notifications, NullLogger<CertificateExpiryService>.Instance);

        return (service, notifications);
    }

    [Fact]
    public async Task CheckAsync_FindsTheBoundCertificate_AndRaisesExpiring_WhenWithinTheWarningWindow()
    {
        var commonName = $"dbdatasync-test-{Guid.NewGuid():N}.example.com";
        var certificate = InstallSelfSigned(commonName);

        // The freshly-issued certificate is ~365 days out; an absurdly large warning window guarantees
        // it falls inside it without this test caring about the exact number.
        var (service, notifications) = BuildService(commonName, expiryWarningDays: 100_000);

        await service.CheckAsync();

        var notification = Assert.Single(notifications.List());
        Assert.Equal(NotificationKinds.CertificateExpiring, notification.Kind);
        Assert.Contains(certificate.Thumbprint, notification.Message);
    }

    [Fact]
    public async Task CheckAsync_WellOutsideTheWarningWindow_RaisesNothing()
    {
        var commonName = $"dbdatasync-test-{Guid.NewGuid():N}.example.com";
        InstallSelfSigned(commonName);

        var (service, notifications) = BuildService(commonName, expiryWarningDays: 1);

        await service.CheckAsync();

        Assert.Empty(notifications.List());
    }

    [Fact]
    public async Task CheckAsync_NoSubjectConfigured_RaisesNothingAndDoesNotThrow()
    {
        var configuration = new ConfigurationBuilder().Build();
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        var notifications = new NotificationStore(database);
        var service = new CertificateExpiryService(
            configuration, new CertificateOptions(), notifications, NullLogger<CertificateExpiryService>.Instance);

        await service.CheckAsync();

        Assert.Empty(notifications.List());
    }

    [Fact]
    public async Task CheckAsync_SubjectConfiguredButNoMatchingCertificate_LogsAndDoesNotThrow()
    {
        var (service, notifications) = BuildService("no-such-certificate.example.com", expiryWarningDays: 30);

        await service.CheckAsync();

        Assert.Empty(notifications.List());
    }
}
