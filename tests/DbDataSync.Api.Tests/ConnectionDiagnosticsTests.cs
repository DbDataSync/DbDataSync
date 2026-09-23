using DbDataSync.Api.Services;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 176M's <see cref="ConnectionDiagnostics"/> — the piece <see cref="ConnectionTestIntegrationTests"/>'s
/// own closed-port test already proves doesn't crash the widened catch in
/// <c>ConnectionsController.Test</c> for an ordinary (non-JDBC) exception. These tests cover
/// <c>Describe</c>'s non-java.sql path directly, plus <c>Redact</c> in isolation — not the
/// <c>java.sql.SQLException</c>-aware branch, which needs IKVM.Java actually loaded in the process (this
/// test project doesn't load it, the same reason <c>Describe</c> itself has to guard against that
/// assembly being absent — see its own doc comment) and so isn't covered here; a real JDBC connection
/// failure exercising that branch has no fixture in this test project today.
/// </summary>
public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public void Describe_AnOrdinaryException_ReturnsToString_NotJustMessage()
    {
        var inner = new InvalidOperationException("the real problem");
        var wrapped = new InvalidOperationException("wrapper message", inner);

        var described = ConnectionDiagnostics.Describe(wrapped);

        // ToString(), not Message — a wrapped exception's inner message must survive, exactly the
        // reason phase 175M's own connect-time validation throws wrapped InvalidOperationExceptions.
        Assert.Contains("wrapper message", described);
        Assert.Contains("the real problem", described);
    }

    /// <summary>The regression this project's own <c>ConnectionTestIntegrationTests.Test_AgainstAClosedPort_ReportsFailureRatherThanThrowing</c>
    /// caught for real: <c>Describe</c> must not throw just because IKVM.Java isn't loaded in this
    /// process (no JDBC driver has ever been used here) — every ordinary .NET exception has to come back
    /// as a string, not propagate a FileNotFoundException for an assembly the caller never asked about.</summary>
    [Fact]
    public void Describe_WithNoJdbcDriverEverLoaded_DoesNotThrowForIkvmBeingAbsent()
    {
        var ex = new TimeoutException("connection timed out");

        var described = ConnectionDiagnostics.Describe(ex);

        Assert.Contains("connection timed out", described);
    }

    [Fact]
    public void Redact_ReplacesEveryOccurrenceOfTheSecret_LeavesEverythingElseIntact()
    {
        const string secret = "hunter2";
        var text = $"connecting with password={secret}; retry password={secret} failed";

        var redacted = ConnectionDiagnostics.Redact(text, secret);

        Assert.DoesNotContain(secret, redacted);
        Assert.Equal(2, redacted.Split("••••••").Length - 1);
        Assert.Contains("connecting with password=", redacted);
        Assert.Contains("retry password=", redacted);
        Assert.Contains("failed", redacted);
    }

    [Fact]
    public void Redact_WithMultipleSecrets_RedactsEachOne()
    {
        var text = "user=alice;password=s3cret";

        var redacted = ConnectionDiagnostics.Redact(text, "alice", "s3cret");

        Assert.DoesNotContain("alice", redacted);
        Assert.DoesNotContain("s3cret", redacted);
        Assert.Contains("user=", redacted);
        Assert.Contains("password=", redacted);
    }

    [Fact]
    public void Redact_WithNullOrEmptySecrets_LeavesTheTextUnchanged()
    {
        const string text = "nothing sensitive here";

        Assert.Equal(text, ConnectionDiagnostics.Redact(text, null, ""));
    }
}
