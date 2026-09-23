namespace DbDataSync.Api.Services;

/// <summary>
/// Phase 176M. What <see cref="Controllers.ConnectionsController.Test"/> uses to widen its own catch (and
/// <c>GenericDriverBase.TestAsync</c>'s) from an allowlist of exception types to "everything except
/// cancellation" without losing diagnostic detail in the process — the two things that widening has to
/// hold at once, or it just trades one bug (an unhandled 500 for an exception type nobody allowlisted)
/// for another (a real diagnostic silently dropped).
/// <para>
/// Lives here, not in <c>DbDataSync.Drivers.Generic</c> (where <c>GenericDriverBase.TestAsync</c>'s own
/// catch lives) — that project has no compile-time visibility into <c>java.sql.SQLException</c>, and
/// can't gain any: it would mean depending on <c>DbDataSync.Drivers.Jdbc</c>, which itself depends on
/// <c>DbDataSync.Drivers.Generic</c>, a cycle. <c>DbDataSync.Api</c> already references
/// <c>DbDataSync.Drivers.Jdbc</c> directly (to host the driver at all), so this is the one place both
/// the java.sql-aware enrichment below and every caller that needs it can actually coexist.
/// </para>
/// </summary>
internal static class ConnectionDiagnostics
{
    /// <summary>
    /// <c>ex.ToString()</c> for anything that isn't a <c>java.sql.SQLException</c> — not
    /// <c>ex.Message</c>, so a wrapped exception (every phase-175M connect-time validation throws an
    /// <see cref="InvalidOperationException"/> around a driver-specific inner exception) doesn't lose the
    /// inner message. <c>java.sql.SQLException</c> carries <c>getSQLState()</c>/<c>getErrorCode()</c>/a
    /// chain via <c>getNextException()</c> (distinct from <c>.Cause</c>/<c>.InnerException</c>) that
    /// vendor JDBC drivers use for multi-part failures — <c>ToString()</c> alone would lose all three.
    /// </summary>
    /// <remarks>
    /// This method runs on **every** driver's failed test, not only JDBC's — <c>ConnectionsController.Test</c>'s
    /// own catch is shared. IKVM.Java is a real, separately-installed library (<c>KnownLibraries</c>'
    /// <c>ikvm</c> entry), not bundled: a process that has never opened a JDBC connection doesn't have it
    /// loaded, and touching <c>java.sql.SQLException</c> at all has to resolve that type to even ask the
    /// question — regardless of <paramref name="ex"/>'s actual type. Found for real, not hypothesized: a
    /// closed-port MsSql test (no JDBC driver anywhere in the process) threw
    /// <see cref="FileNotFoundException"/> for <c>IKVM.Java</c> here. Same "an assembly couldn't be
    /// loaded" exception set <c>DriverLoader.LoadCompiledDrivers</c>'s own catch already treats this way
    /// elsewhere in this repo — not a one-off choice.
    /// <para>
    /// **The java.sql-touching code has to live in its own method, not inline in this one's try block.**
    /// The JIT resolves every type a method's IL references while compiling that method — before any of
    /// its own try/catch has a chance to run — so a missing-assembly failure for a type referenced
    /// *inside* this method's own try block still surfaces as if there were no try/catch there at all.
    /// Isolating the reference in <see cref="TryDescribeSqlException"/> defers that resolution to when
    /// *that* method is first called, which — because the call itself happens inside this method's try —
    /// is a call the surrounding catch can actually see fail. Confirmed this is real, not a theory: the
    /// inline version reproduced the exact same unhandled <see cref="FileNotFoundException"/> even with
    /// the identical catch clause wrapped directly around it.
    /// </para>
    /// </remarks>
    public static string Describe(Exception ex)
    {
        try
        {
            if (TryDescribeSqlException(ex, out var described))
                return described;
        }
        catch (Exception loadEx) when (loadEx is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // IKVM.Java isn't loaded in this process — see the remarks above. ex can't actually be a
            // java.sql.SQLException if the runtime can't even load that type, so falling through to the
            // generic ToString() below is correct, not a fallback of last resort.
        }
        return ex.ToString();
    }

    private static bool TryDescribeSqlException(Exception ex, out string described)
    {
        if (ex is java.sql.SQLException sql)
        {
            var parts = new List<string>();
            for (var e = sql; e is not null; e = e.getNextException())
                parts.Add($"{e.Message} (SQLState={e.getSQLState()}, ErrorCode={e.getErrorCode()})");
            described = string.Join(" | ", parts);
            return true;
        }
        described = "";
        return false;
    }

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
