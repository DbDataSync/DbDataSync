using System.Data.Common;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// A driver that can prove a configured connection actually reaches its engine.
/// <para>
/// Opt-in, in the same shape as <see cref="ISegmentExpandingReader"/>: a driver reaching an arbitrary
/// engine through ODBC or JDBC may have no probe it can name, and requiring one would mean
/// implementing a method it cannot honour. Callers ask <c>driver is IConnectionTester</c>, and
/// <see cref="DriverCapabilities.SupportsConnectionTest"/> reports the answer so a UI can hide an
/// affordance that could never work rather than offer one that always fails.
/// </para>
/// </summary>
public interface IConnectionTester
{
    /// <summary>Runs the probe on an already-open connection. Must not throw for an engine-side
    /// failure — an unreachable or unhealthy database is an answer, not an exception.</summary>
    Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken);
}

/// <param name="RoundTrip">Time for the probe itself. The caller adds connect time separately, since
/// a driver testing an already-open connection cannot see it.</param>
/// <param name="Error">The provider's own message on failure. Deliberately raw: this is an operator
/// console, and the provider's wording is most of the diagnosis.</param>
public sealed record ConnectionTestResult(bool Succeeded, TimeSpan RoundTrip, string? ServerVersion, string? Error);
