using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api.Configuration;
using DbDataSync.Certificates;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Checks the bound certificate once a day and raises through the existing notification core — see the
/// phase 82 doc's "Expiry." On the same shape as <see cref="SchedulerService"/> and
/// <see cref="RunPruningService"/>: an immediate first pass, then a fixed-interval
/// <see cref="PeriodicTimer"/>, swallowing and logging rather than letting one bad pass take the
/// background service down.
/// <para>
/// **A new constant and a producer, not a new mechanism** — <see cref="NotificationKinds.CertificateExpiring"/>
/// and <see cref="NotificationKinds.CertificateExpired"/> are the constants; this class is the producer.
/// "Warn before X expires" is the shape phases 77 and 80 already built for watermark position and pause
/// duration (<c>NotificationKinds.PositionExpired</c>, <c>ReplicationPaused</c>); inventing a second path
/// for the same shape would have been the mistake the doc calls out.
/// </para>
/// <para>
/// **Registered only when <see cref="OperatingSystem.IsWindows"/>** (<c>DbDataSyncHost.Build</c>), the
/// same pattern that file already uses for <c>Negotiate</c> authentication — not a new idiom. Certificate
/// management is Windows-only end to end (the doc's own scope line: "Certificate management on Linux or
/// macOS... reports unavailable"), so a Linux host never constructs this type at all rather than
/// constructing it and having it no-op; <see cref="ExecuteAsync"/>'s own <c>IsWindows</c> check is
/// defence in depth for the one path that could still reach it — a test host that registers hosted
/// services unconditionally.
/// </para>
/// </summary>
public sealed class CertificateExpiryService(
    IConfiguration configuration,
    CertificateOptions options,
    NotificationStore notifications,
    ILogger<CertificateExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    // In-memory, not persisted — a restart mid-day can produce one extra notification, which
    // CertificateExpiryEvaluator's own doc comment accepts as the same courtesy-cap tradeoff every
    // other NotificationStore producer already makes rather than a guarantee worth a database cursor.
    private DateOnly? _lastRaisedExpiringUtc;
    private DateOnly? _lastRaisedExpiredUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation(
                "Certificate expiry checking is Windows-only; this host is not Windows, so it is disabled.");
            return;
        }

        await CheckAsync();

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CheckAsync();
    }

    /// <summary>One pass. Public so a test can drive it directly rather than waiting on the
    /// timer.</summary>
    public Task CheckAsync()
    {
        // Repeats ExecuteAsync's own guard rather than relying on it — CheckAsync is public and callable
        // directly (by a test, or a future caller), and the CA1416 platform-compatibility analyzer needs
        // the guard textually wrapping the Windows-only call to recognise it as one, not just a check
        // that happens to run somewhere earlier in the call chain.
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        try
        {
            CheckOnWindows();
        }
        catch (Exception ex)
        {
            // Logged and swallowed, deliberately: an unreadable store or a transient state-database
            // problem is a reason to try again tomorrow, not a reason to take down a background service
            // the process never restarts — the same call RunPruningService's own doc comment makes for
            // exactly this shape of failure.
            logger.LogWarning(ex, "Certificate expiry check failed; it will be retried tomorrow.");
        }

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void CheckOnWindows()
    {
        var section = configuration.GetSection(CertificateBinding.Section);
        var subject = section["Subject"];
        if (string.IsNullOrWhiteSpace(subject))
            return; // Nothing bound yet — dbdatasync config cert bind hasn't run, or config sets no certificate.

        var location = Enum.TryParse<StoreLocation>(section["Location"], ignoreCase: true, out var parsedLocation)
            ? parsedLocation
            : StoreLocation.LocalMachine;

        var certificate = CertificateStore.FindBySubject(subject, location);
        if (certificate is null)
        {
            logger.LogWarning(
                "The bound certificate (subject '{Subject}') was not found in {Location}\\My.", subject, location);
            return;
        }

        EvaluateExpiry(certificate);
        EvaluateKeyAccess(certificate, subject);
    }

    private void EvaluateExpiry(X509Certificate2 certificate)
    {
        var now = DateTimeOffset.UtcNow;
        var decision = CertificateExpiryEvaluator.Evaluate(
            certificate.NotAfter, options.ExpiryWarningDays, now, _lastRaisedExpiringUtc, _lastRaisedExpiredUtc);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        switch (decision)
        {
            case CertificateExpiryDecision.Expiring:
                var daysRemaining = (int)Math.Floor((certificate.NotAfter - now).TotalDays);
                notifications.Raise(
                    NotificationKinds.CertificateExpiring,
                    $"The bound TLS certificate ('{certificate.Subject}', thumbprint {certificate.Thumbprint}) " +
                    $"expires in {daysRemaining} day(s), on {certificate.NotAfter:yyyy-MM-dd}. Renew it with " +
                    "'dbdatasync config cert renew'.");
                _lastRaisedExpiringUtc = today;
                break;

            case CertificateExpiryDecision.Expired:
                notifications.Raise(
                    NotificationKinds.CertificateExpired,
                    $"The bound TLS certificate ('{certificate.Subject}', thumbprint {certificate.Thumbprint}) " +
                    $"expired on {certificate.NotAfter:yyyy-MM-dd}. Every browser will now refuse it. Renew it " +
                    "with 'dbdatasync config cert renew'.");
                _lastRaisedExpiredUtc = today;
                break;

            case CertificateExpiryDecision.None:
                break;
        }
    }

    /// <summary>
    /// The other half of the daily pass: can the account this process's service is configured to run
    /// as still read the private key. An ACL can be removed by a certificate re-issue or a group policy
    /// — see <see cref="PrivateKeyAccess"/>'s own doc comment — and that failure is otherwise invisible
    /// until the next restart produces a TLS handshake failure naming no permission problem at all.
    /// <para>
    /// **A logged warning, not a third notification kind.** The phase 82 doc frames the new
    /// notification surface as exactly two constants (<c>CertificateExpiring</c>/<c>CertificateExpired</c>);
    /// this check is real but narrower in audience — an operator watching logs or Windows Event
    /// forwarding, not necessarily the notification feed a viewer reads — so it stays a log line rather
    /// than inventing a kind the doc never asked for.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void EvaluateKeyAccess(X509Certificate2 certificate, string subject)
    {
        var account = InstalledServiceAccount.Resolve(ServiceCommandName) ?? "LocalSystem";
        if (string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return; // LocalSystem already has access to every LocalMachine\My private key — nothing to check.

        if (!PrivateKeyAccess.CanRead(certificate, account))
        {
            logger.LogWarning(
                "The service account '{Account}' cannot read the private key of the bound certificate " +
                "(subject '{Subject}', thumbprint {Thumbprint}). The next service restart will fail its TLS " +
                "handshake. Run 'dbdatasync config cert status' for details, or re-grant access.",
                account, subject, certificate.Thumbprint);
        }
    }

    /// <summary>The Windows service name <c>dbdatasync service install</c> registers under — duplicated
    /// as a literal rather than referencing <c>DbDataSync.Cli.ServiceCommand.ServiceName</c>, since
    /// <c>DbDataSync.Api</c> does not (and should not) depend on the CLI project.</summary>
    private const string ServiceCommandName = "DbDataSync";
}
