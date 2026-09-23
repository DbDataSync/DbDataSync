using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Phase 176M. A driver that can show what it would actually resolve a connection to — the same
/// opt-in-interface shape <see cref="IConnectionTester"/> already uses, for the same reason: not every
/// driver has a meaningful notion of "the resolved connection string" (a compiled plugin driver might
/// build one in a way this shape can't express), and requiring one would mean implementing a method some
/// drivers cannot honour.
/// </summary>
public interface IConnectionPreviewer
{
    /// <summary>Never touches the network — this only resolves and assembles, the same unification
    /// <see cref="IDriver.CreateConnection"/> does, with a masked placeholder standing in for the real
    /// credential wherever one would go. Never the real secret, so a redaction bug downstream of this
    /// call can't leak it.</summary>
    ConnectionPreview PreviewConnection(ConnectionConfig connection);
}

/// <param name="ConnectionString">The resolved ADO.NET-shaped connection string — meaningful for every
/// driver, JDBC included (JDBC's own intermediate <see cref="System.Data.Common.DbConnectionStringBuilder"/>
/// unification, not the final JDBC URL).</param>
/// <param name="JdbcUri">The resolved JDBC URL, or null for a driver with no such notion (every
/// non-JDBC driver today).</param>
/// <param name="Properties">Whatever reached the driver outside <paramref name="ConnectionString"/>/
/// <paramref name="JdbcUri"/> — for JDBC, the <c>java.util.Properties</c> bag actually handed to
/// <c>driver.connect()</c>. Always empty for a plain ADO.NET <c>GenericDriver</c> today (nothing routes
/// through an out-of-connection-string channel there) — kept as a real, reusable field rather than
/// JDBC-only, in case a future ADO.NET driver needs it.</param>
public sealed record ConnectionPreview(string ConnectionString, string? JdbcUri, IReadOnlyDictionary<string, string> Properties);
