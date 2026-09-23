using System.Data.Common;

namespace DbDataSync.Drivers.Jdbc.Ado;

/// <summary>
/// One link in a <c>java.sql.SQLException</c> chain (<c>getNextException()</c>), translated to plain
/// .NET data at the point of translation — no <c>java.sql</c> type anywhere in this record's own
/// signature, deliberately, so a caller holding one never needs IKVM visibility to read it.
/// </summary>
public sealed record JdbcSqlError(string Message, string? SqlState, int ErrorCode);

/// <summary>
/// The one and only place a raw <c>java.sql.SQLException</c> is allowed to exist as a live object in
/// this codebase — everywhere else, a JDBC-originated ADO.NET failure is this, a plain
/// <see cref="DbException"/> subtype carrying the same <c>SQLState</c>/<c>ErrorCode</c>/chained-message
/// detail as plain <see cref="string"/>/<see cref="int"/> data.
/// <para>
/// **Why this exists, not a shared class in `DbDataSync.Api` that pattern-matches `java.sql.SQLException`
/// directly (an earlier, wrong version of this same idea):** IKVM.Java is a real, separately-installed
/// library (`KnownLibraries`' `ikvm` entry), not bundled — a process that has never opened a JDBC
/// connection doesn't have it loaded, and merely touching the *type* `java.sql.SQLException` (even inside
/// a guarded `try`/`catch`) forces the CLR to resolve it, regardless of the exception actually being
/// inspected. Worse, that resolution happens while the JIT compiles the *whole enclosing method*, before
/// that method's own `try`/`catch` runs — so a missing-assembly failure for a type referenced inside a
/// method's own try block isn't even catchable there. An ordinary MsSql closed-port test (no JDBC
/// anywhere in that process) proved this for real: an unhandled `FileNotFoundException` for `IKVM.Java`,
/// from code that had nothing to do with JDBC.
/// </para>
/// <para>
/// The fix is this type, not a defensive guard around the old one: <c>java.sql.SQLException</c> is
/// translated to this **here**, in <c>DbDataSync.Drivers.Jdbc</c> — the one project that already,
/// unconditionally, depends on IKVM (it can't function without it) — at every boundary where a
/// <c>java.sql</c> call could throw one (<see cref="JdbcProviderFactory.GetJdbcConnection"/>,
/// <c>JdbcCommand</c>'s execute methods, <c>JdbcDataReader.Read</c>). Every exception this type's own
/// factory method touches a live <c>java.sql.SQLException</c> that already exists as a real object —
/// IKVM is necessarily already loaded to have produced it, so there is no defensive guard to write here
/// at all. Shared code downstream (<c>GenericDriverBase.TestAsync</c>, <c>ConnectionsController.Test</c>,
/// <c>ConnectionDiagnostics</c>) never touches <c>java.sql</c> anything, ever — it sees an ordinary
/// <see cref="DbException"/>, the same contract every other ADO.NET provider in this repo already honors.
/// </para>
/// </summary>
public sealed class JdbcSqlException : DbException
{
    public IReadOnlyList<JdbcSqlError> Errors { get; }

    public JdbcSqlException(IReadOnlyList<JdbcSqlError> errors)
        : base(Describe(errors))
    {
        Errors = errors;
    }

    private static string Describe(IReadOnlyList<JdbcSqlError> errors) =>
        string.Join(" | ", errors.Select(e => $"{e.Message} (SQLState={e.SqlState}, ErrorCode={e.ErrorCode})"));

    /// <summary>Walks <c>getNextException()</c> to capture every link in the chain — the whole reason
    /// this type exists rather than just letting <c>ex.ToString()</c> stand in for it, since a vendor
    /// JDBC driver uses this chain for genuinely multi-part failures a single message would lose.</summary>
    internal static JdbcSqlException FromJava(java.sql.SQLException sql)
    {
        var errors = new List<JdbcSqlError>();
        for (var e = sql; e is not null; e = e.getNextException())
            errors.Add(new JdbcSqlError(e.Message, e.getSQLState(), e.getErrorCode()));
        return new JdbcSqlException(errors);
    }
}
