using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;

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
