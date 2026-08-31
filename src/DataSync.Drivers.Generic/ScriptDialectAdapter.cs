using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

/// <summary>
/// A driver that can say which <see cref="SqlDialect"/> it speaks.
/// <para>
/// Opt-in by interface, like <c>ISegmentExpandingReader</c> and <c>IConnectionTester</c> — an ODBC or
/// JDBC driver reaching an arbitrary engine may have no single dialect to name. Callers ask
/// <c>driver is IDialectProvider</c>.
/// </para>
/// </summary>
public interface IDialectProvider
{
    SqlDialect Dialect { get; }
}

/// <summary>
/// A driver that can hand out the catalog its own components use.
/// <para>
/// Opt-in for the same reason as <see cref="IDialectProvider"/>, and useful for the same kind of
/// caller: something composing a generic component *for* a driver from outside it — phase 30's
/// <c>ScriptedQuery</c> reader, which cannot be built inside a driver project because it needs the
/// script host.
/// </para>
/// <para>
/// It is also the seam a per-connection catalog override would use, which phase 29 recorded as needing
/// per-connection driver components. Nothing does that yet.
/// </para>
/// </summary>
public interface ITableCatalogProvider
{
    ITableCatalog Catalog { get; }
}

/// <summary>
/// Presents a <see cref="SqlDialect"/> to a script through the narrow <see cref="IScriptDialect"/> view.
/// <para>
/// The adapter exists so the script surface can stay still while <see cref="SqlDialect"/> keeps moving —
/// it has gained hooks in phases 17, 18, 20 and 25, and a script written today should still compile when
/// it gains another.
/// </para>
/// </summary>
public sealed class ScriptDialectAdapter(SqlDialect dialect, string engineName) : IScriptDialect
{
    /// <summary>
    /// The dialect a driver speaks, or null when it does not name one. Replaces the hardcoded
    /// driver-type switch this used to be: a new driver now supplies its own dialect by implementing
    /// <see cref="IDialectProvider"/>, rather than by someone remembering to extend a switch in two
    /// processes.
    /// </summary>
    public static IScriptDialect? For(IDriver driver) =>
        driver is IDialectProvider provider
            ? new ScriptDialectAdapter(provider.Dialect, driver.DriverType.ToString())
            : null;

    public string EngineName => engineName;

    public string QuoteIdentifier(string identifier) => dialect.QuoteIdentifier(identifier);

    public string ParameterReference(string name) => dialect.ParameterReference(name);

    public CanonicalType ToCanonicalType(string nativeType) => dialect.ToCanonicalType(nativeType);

    public RenderedColumnType RenderColumnType(CanonicalType type) => dialect.RenderColumnType(type);
}
