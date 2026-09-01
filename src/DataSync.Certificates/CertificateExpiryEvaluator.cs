namespace DataSync.Certificates;

/// <summary>What today's expiry check should raise, if anything.</summary>
public enum CertificateExpiryDecision
{
    None,
    Expiring,
    Expired,
}

/// <summary>
/// Pure decision logic for the daily expiry check — see the phase 82 doc's "Expiry" section. Deliberately
/// has no dependency on <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>,
/// <c>NotificationStore</c>, or the clock beyond what is passed in, so the exact scenarios the doc's own
/// test plan names ("31 days out raises nothing," "29 days raises once," "a second check the same day
/// raises nothing further," "past NotAfter raises Expired") are one call each, with no certificate, no
/// database, and no Windows to stand up.
/// <para>
/// **The caller owns "once a day."** This method is given the date it last raised each kind (null if
/// never) and decides fresh each call — it does not remember anything itself. <see
/// cref="CertificateExpiryService"/> is the one caller, and keeps that state in memory for the life of
/// the process; a restart mid-day can produce one extra notification, which is the same courtesy-cap
/// tradeoff <c>NotificationStore</c>'s producers already accept elsewhere (a notification is not an
/// audit trail — see <c>TaskRunStore.NotifyIfNewlyPaused</c>'s own doc comment) rather than a guarantee
/// worth a persisted cursor for.
/// </para>
/// </summary>
public static class CertificateExpiryEvaluator
{
    public static CertificateExpiryDecision Evaluate(
        DateTimeOffset notAfter, int warningDays, DateTimeOffset now,
        DateOnly? lastRaisedExpiringUtc, DateOnly? lastRaisedExpiredUtc)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (now >= notAfter)
            return lastRaisedExpiredUtc == today ? CertificateExpiryDecision.None : CertificateExpiryDecision.Expired;

        var daysRemaining = (notAfter - now).TotalDays;
        if (daysRemaining <= warningDays)
        {
            return lastRaisedExpiringUtc == today
                ? CertificateExpiryDecision.None
                : CertificateExpiryDecision.Expiring;
        }

        return CertificateExpiryDecision.None;
    }
}
