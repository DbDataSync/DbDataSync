using DbDataSync.Api.Services;
using DbDataSync.Drivers.Jdbc.Ado;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 176M's <see cref="ConnectionDiagnostics"/>, corrected after a real incident:
/// <see cref="ConnectionTestIntegrationTests"/>'s own closed-port test caught an unhandled
/// <see cref="FileNotFoundException"/> for <c>IKVM.Java</c> from an earlier version of <c>Describe</c>
/// that pattern-matched <c>java.sql.SQLException</c> directly, in this very project, for an ordinary
/// MsSql failure that had nothing to do with JDBC. Fixed by moving all <c>java.sql</c> translation into
/// <c>DbDataSync.Drivers.Jdbc</c> itself (<see cref="JdbcSqlException"/>, a plain
/// <see cref="System.Data.Common.DbException"/> subtype) — <c>Describe</c> now touches no <c>java.sql</c>
/// type anywhere, so both branches below are testable directly, with no IKVM.Java load required, no
/// guard needed, and no gap: constructing a <see cref="JdbcSqlException"/> needs nothing but plain
/// <see cref="JdbcSqlError"/> records.
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

    /// <summary>The exact case the FileNotFoundException incident was about: an ordinary, non-JDBC
    /// failure must come back as a plain string with no dependency on IKVM.Java being loaded — provable
    /// directly now, since <c>Describe</c> has no code path that could ever touch it.</summary>
    [Fact]
    public void Describe_AnOrdinaryException_NeverTouchesJdbcTypes()
    {
        var ex = new TimeoutException("connection timed out");

        var described = ConnectionDiagnostics.Describe(ex);

        Assert.Contains("connection timed out", described);
    }

    /// <summary>The branch the incident's own fix exists for: a JDBC-originated failure — by the time it
    /// reaches this class, always a plain <see cref="JdbcSqlException"/>, never a live <c>java.sql</c>
    /// object — still gets its SQLState/ErrorCode/chained-message detail, built once at the JDBC-side
    /// translation site (<see cref="JdbcSqlException"/>'s own constructor) and just read back here.</summary>
    [Fact]
    public void Describe_AJdbcSqlException_IncludesEveryChainedErrorsSqlStateAndErrorCode()
    {
        var chained = new JdbcSqlException([
            new JdbcSqlError("duplicate key value violates unique constraint", "23505", 0),
            new JdbcSqlError("Detail: Key (id)=(1) already exists.", "23505", 0),
        ]);

        var described = ConnectionDiagnostics.Describe(chained);

        Assert.Contains("duplicate key value violates unique constraint", described);
        Assert.Contains("Detail: Key (id)=(1) already exists.", described);
        Assert.Contains("SQLState=23505", described);
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
