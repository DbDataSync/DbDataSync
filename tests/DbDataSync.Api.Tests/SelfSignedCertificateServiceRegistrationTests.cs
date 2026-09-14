using DbDataSync.Api.Services;
using DbDataSync.Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 130, tier 2 — <see cref="SelfSignedCertificateService"/> is registered only when
/// <c>Kestrel:Certificates:Default:Path</c> actually names this phase's own well-known managed path
/// (<c>DbDataSyncHost.Build</c>), not gated by OS the way <c>CertificateExpiryService</c> is. Calls
/// <see cref="DbDataSyncHost.Build"/> directly rather than running it — the same approach
/// <see cref="DbDataSyncConfigFilePrecedenceTests"/> already uses to prove something about the built
/// container without starting a real Kestrel listener.
/// </summary>
public sealed class SelfSignedCertificateServiceRegistrationTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-selfsigned-registration-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string[] BaseArgs(params string[] extra) =>
    [
        "--DbDataSync:RepoRoot", _repoRoot,
        "--DbDataSync:StateDbPath", Path.Combine(_repoRoot, "state.db"),
        "--DbDataSync:TaskRunnerDllPath", Path.Combine(_repoRoot, "DbDataSync.TaskRunner.dll"),
        "--DbDataSync:Auth:Disabled", "true",
        .. extra,
    ];

    [Fact]
    public void KestrelPathPointsAtTheManagedCertificate_ServiceIsRegistered()
    {
        var managedPath = ManagedSelfSignedCertificate.PfxPath(_repoRoot);

        using var app = DbDataSyncHost.Build(BaseArgs("--Kestrel:Certificates:Default:Path", managedPath));

        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is SelfSignedCertificateService);
    }

    [Fact]
    public void NoCertificateConfigured_ServiceIsNotRegistered()
    {
        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is SelfSignedCertificateService);
    }

    [Fact]
    public void KestrelPathPointsAtAnOperatorSuppliedCertificate_ServiceIsNotRegistered()
    {
        using var app = DbDataSyncHost.Build(
            BaseArgs("--Kestrel:Certificates:Default:Path", Path.Combine(_repoRoot, "operator-supplied.pfx")));

        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is SelfSignedCertificateService);
    }
}
