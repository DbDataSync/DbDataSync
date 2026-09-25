using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// A driver that can say which <see cref="SqlDialect"/> it speaks.
/// <para>
/// Opt-in by interface, like <see cref="ISegmentExpandingReader"/> and <see cref="IConnectionTester"/> —
/// an ODBC or JDBC driver reaching an arbitrary engine may have no single dialect to name. Callers ask
/// <c>driver is IDialectProvider</c>.
/// </para>
/// </summary>
public interface IDialectProvider
{
    SqlDialect Dialect { get; }
}
