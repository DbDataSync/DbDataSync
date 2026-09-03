namespace DbDataSync.Certificates.Tests;

/// <summary>The doc's own four scenarios, verbatim: 31 days out raises nothing, 29 days raises once, a
/// second check the same day raises nothing further, past NotAfter raises Expired.</summary>
public sealed class CertificateExpiryEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThirtyOneDaysOut_RaisesNothing()
    {
        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(31), warningDays: 30, Now, lastRaisedExpiringUtc: null, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.None, decision);
    }

    [Fact]
    public void TwentyNineDaysOut_RaisesExpiringOnce()
    {
        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(29), warningDays: 30, Now, lastRaisedExpiringUtc: null, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.Expiring, decision);
    }

    [Fact]
    public void SecondCheckSameDay_RaisesNothingFurther()
    {
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(29), warningDays: 30, Now, lastRaisedExpiringUtc: today, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.None, decision);
    }

    [Fact]
    public void NextDayStillWithinWindow_RaisesExpiringAgain()
    {
        var yesterday = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1);

        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(29), warningDays: 30, Now, lastRaisedExpiringUtc: yesterday, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.Expiring, decision);
    }

    [Fact]
    public void PastNotAfter_RaisesExpired()
    {
        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(-1), warningDays: 30, Now, lastRaisedExpiringUtc: null, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.Expired, decision);
    }

    [Fact]
    public void PastNotAfter_SecondCheckSameDay_RaisesNothingFurther()
    {
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        var decision = CertificateExpiryEvaluator.Evaluate(
            Now.AddDays(-1), warningDays: 30, Now, lastRaisedExpiringUtc: null, lastRaisedExpiredUtc: today);

        Assert.Equal(CertificateExpiryDecision.None, decision);
    }

    [Fact]
    public void ExactlyAtNotAfter_CountsAsExpired()
    {
        var decision = CertificateExpiryEvaluator.Evaluate(
            Now, warningDays: 30, Now, lastRaisedExpiringUtc: null, lastRaisedExpiredUtc: null);

        Assert.Equal(CertificateExpiryDecision.Expired, decision);
    }
}
