using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api.Configuration;
using DbDataSync.Certificates;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Phase 130 — tier 2's renewal half: keeps a managed self-signed certificate
/// (<see cref="ManagedSelfSignedCertificate"/>) current the way <see cref="CertificateExpiryService"/>
/// keeps a Windows store-based one watched. Same shape as that class, <see cref="SchedulerService"/> and
/// <see cref="RunPruningService"/>: an immediate first pass, then a daily <see cref="PeriodicTimer"/>,
/// swallowing and logging rather than taking the process down.
/// <para>
/// **Acts, rather than only warning.** A Windows store-based certificate is renewed by hand
/// (<c>cert renew</c>); this service regenerates <see cref="ManagedSelfSignedCertificate.PfxPath"/> in
/// place once the bound certificate is within a third of its own lifetime of expiring, then raises
/// <see cref="NotificationKinds.CertificateExpiring"/> noting that a restart is still needed to pick it
/// up — see the phase 130 doc's "no live reload" decision, which this phase does not revisit.
/// </para>
/// <para>
/// **Registered only when tier 2 is actually configured** (<c>DbDataSyncHost.Build</c>, gated on
/// <c>Kestrel:Certificates:Default:Path</c> equalling this phase's own well-known path) — not gated by
/// OS, unlike <see cref="CertificateExpiryService"/>: tier 2's whole point is a certificate story that
/// works on Linux. Never touches a certificate at any other path, including one <c>use-pem</c>/
/// <c>use-pfx</c> pointed at — that path belongs to phase 113 alone.
/// </para>
/// </summary>
public sealed class SelfSignedCertificateService(
    ApiOptions apiOptions, IConfiguration configuration, NotificationStore notifications,
    ILogger<SelfSignedCertificateService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckAsync();

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CheckAsync();
    }

    /// <summary>One pass. Public so a test can drive it directly rather than waiting on the
    /// timer.</summary>
    public Task CheckAsync()
    {
        try
        {
            Check();
        }
        catch (Exception ex)
        {
            // Logged and swallowed, deliberately — the same call CertificateExpiryService's own doc
            // comment makes for exactly this shape of failure: a transient problem is a reason to try
            // again tomorrow, not a reason to take down a background service the process never
            // restarts.
            logger.LogWarning(ex, "Managed self-signed certificate check failed; it will be retried tomorrow.");
        }

        return Task.CompletedTask;
    }

    private void Check()
    {
        var path = ManagedSelfSignedCertificate.PfxPath(apiOptions.RepoRoot);
        if (!File.Exists(path))
            return; // Nothing generated yet, or the file was removed out from under this service.

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        if (!ManagedSelfSignedCertificate.ShouldRenew(certificate.NotBefore, certificate.NotAfter, DateTimeOffset.UtcNow))
            return;

        var validityDays = Math.Max(1, (int)Math.Round((certificate.NotAfter - certificate.NotBefore).TotalDays));
        using var renewed = ManagedSelfSignedCertificate.Generate(ConsoleHost(), validityDays);
        ManagedSelfSignedCertificate.Write(apiOptions.RepoRoot, renewed);

        notifications.Raise(
            NotificationKinds.CertificateExpiring,
            $"The managed self-signed TLS certificate was renewed (new expiry {renewed.NotAfter:yyyy-MM-dd}). " +
            "Restart DbDataSync to serve it — nothing takes effect until then.");
    }

    private string ConsoleHost()
    {
        var url = configuration["DbDataSync:App:Url"] ?? ApiOptions.DefaultUrl;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "localhost";
    }
}
