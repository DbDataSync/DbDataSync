using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Api.Services;

/// <summary>
/// On-demand schema introspection for a configured connection (architecture/detailed-design.md §3.1)
/// — opens a connection, runs one metadata query, closes it. No data movement.
/// <para>
/// Every answer goes through <see cref="ScriptedMetadata"/>, so a connection with a bound
/// <c>metadataProvider</c> script answers with what that script says and one without falls straight
/// through to the driver at no cost. This is the surface an operator *browses and maps against*; the
/// pipeline reads the driver's own catalog — see phase 29.
/// </para>
/// </summary>
public sealed class MetadataService(DriverConnectionFactory connections, ScriptedMetadata scriptedMetadata)
{
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionName, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListDatabasesAsync(
                connectionName, connection, driver, DialectFor(driver), cancellationToken);
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(string connectionName, string database, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListTablesAsync(
                connectionName, connection, driver, DialectFor(driver), database, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListColumnsAsync(
                connectionName, connection, driver, DialectFor(driver), database, schema, table, cancellationToken);
    }

    /// <summary>
    /// A driver that names no dialect still browses fine — the dialect is only there for a script that
    /// wants to quote or translate a type, and a script bound to such a connection will find out when it
    /// asks. Failing here would stop the pickers working for a driver that has no scripts at all.
    /// </summary>
    private static IScriptDialect DialectFor(IDriver driver) =>
        ScriptDialectAdapter.For(driver) ?? DialectlessScriptDialect.Instance;
}

/// <summary>Stands in for a driver that names no dialect — ODBC and JDBC reaching an arbitrary engine.
/// Every member throws, so a script that needs one is told plainly rather than silently given SQL
/// Server's.</summary>
internal sealed class DialectlessScriptDialect : IScriptDialect
{
    public static DialectlessScriptDialect Instance { get; } = new();

    public string EngineName => "Unknown";

    public string QuoteIdentifier(string identifier) => throw Unsupported();
    public string ParameterReference(string name) => throw Unsupported();
    public CanonicalType ToCanonicalType(string nativeType) => throw Unsupported();
    public RenderedColumnType RenderColumnType(CanonicalType type) => throw Unsupported();

    private static InvalidOperationException Unsupported() =>
        new("This connection's driver does not name a SQL dialect, so a script cannot generate SQL for it.");
}
