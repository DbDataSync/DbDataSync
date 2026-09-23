using DbDataSync.Drivers.Jdbc.Ado;

namespace DbDataSync.Api.Services;

/// <summary>
/// Phase 176M. What <see cref="Controllers.ConnectionsController.Test"/> uses to widen its own catch (and
/// <c>GenericDriverBase.TestAsync</c>'s) from an allowlist of exception types to "everything except
/// cancellation" without losing diagnostic detail in the process — the two things that widening has to
/// hold at once, or it just trades one bug (an unhandled 500 for an exception type nobody allowlisted)
/// for another (a real diagnostic silently dropped).
/// <para>
/// **Touches no <c>java.sql</c> type, anywhere, ever — deliberately.** An earlier version of this class
/// pattern-matched <c>java.sql.SQLException</c> directly, on the theory that <c>DbDataSync.Api</c>
/// already references <c>DbDataSync.Drivers.Jdbc</c> (to host the driver) so it "has" IKVM visibility.
/// That was the wrong boundary: IKVM.Java is a real, separately-installed library
/// (<c>KnownLibraries</c>' <c>ikvm</c> entry), not bundled, and a process that has never opened a JDBC
/// connection doesn't have it loaded — merely touching the *type* <c>java.sql.SQLException</c> (even
/// inside a guarded <c>try</c>/<c>catch</c>, even in its own isolated method) forces the CLR to resolve
/// it, regardless of what exception is actually being inspected. An ordinary MsSql closed-port test (no
/// JDBC anywhere in that process) proved this for real: an unhandled <see cref="FileNotFoundException"/>
/// for <c>IKVM.Java</c>, from code with nothing to do with JDBC.
/// </para>
/// <para>
/// The real fix lives in <c>DbDataSync.Drivers.Jdbc</c> — the one project that already, unconditionally,
/// depends on IKVM — where every <c>java.sql</c> call that can throw is wrapped and translated into
/// <see cref="JdbcSqlException"/>, a plain <see cref="System.Data.Common.DbException"/> subtype carrying
/// the same detail as plain <see cref="string"/>/<see cref="int"/> fields. This class only ever sees
/// that: ordinary .NET data, no live <c>java.sql</c> object and no reference to any <c>java.sql</c> type
/// in its own code, so there is nothing here that can fail to load. See
/// <see cref="JdbcSqlException"/>'s own doc comment for the full reasoning.
/// </para>
/// </summary>
internal static class ConnectionDiagnostics
{
    /// <summary>
    /// <see cref="JdbcSqlException.Message"/> (already built from its own <c>SqlState</c>/<c>ErrorCode</c>/
    /// chain data) for a JDBC-originated failure, <c>ex.ToString()</c> for anything else — not
    /// <c>ex.Message</c>, so a wrapped exception (every phase-175M connect-time validation throws an
    /// <see cref="InvalidOperationException"/> around a driver-specific inner exception) doesn't lose the
    /// inner message.
    /// </summary>
    public static string Describe(Exception ex) => ex is JdbcSqlException jdbc ? jdbc.Message : ex.ToString();

    /// <summary>
    /// By literal secret value, applied once at the boundary — deliberately not "only show
    /// <see cref="System.Data.Common.DbException"/> messages" or "omit the field entirely," either of
    /// which is exactly the mechanism that was dropping the real diagnostic in the first place. Redacts
    /// the one specific known-sensitive substring per call; everything else (SQLState, error codes, the
    /// real host/port/database/URL shape, chained messages) stays intact. <c>null</c>/empty secrets are
    /// skipped rather than replacing every occurrence of an accidentally-empty string.
    /// </summary>
    public static string Redact(string text, params string?[] secrets)
    {
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, "••••••");
        return text;
    }
}
