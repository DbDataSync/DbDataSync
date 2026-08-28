using System.Data.Common;
using DataSync.Drivers.Abstractions;

namespace DataSync.Scripting.Abstractions;

/// <summary>
/// Answers "what databases, tables and columns does this connection have", in place of the driver's own
/// catalog.
/// <para>
/// This is the one contract that hands a script a live <see cref="DbConnection"/>, because there is no
/// way to describe "ask the catalog" as data. Everything else a script does returns a description and
/// lets the host act on it.
/// </para>
/// <para>
/// It governs what an operator **browses and can map** — the SPA's cascading pickers — and not what the
/// pipeline reads when it builds a staging table or resolves a segment column. See phase 29 for why the
/// two are separate, and for how a *synthetic* column surfaced here gets its value from the transform
/// slots without the pipeline's catalog ever needing to know about it.
/// </para>
/// </summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext context, CancellationToken cancellationToken);

    Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        MetadataContext context, string database, CancellationToken cancellationToken);

    Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        MetadataContext context, string database, string schema, string table, CancellationToken cancellationToken);
}

/// <param name="Connection">
/// Open, and already pointed at the requested database where the engine has that notion. A script may
/// run its own queries on it; it must not dispose it or leave a reader open.
/// </param>
/// <param name="DriverDatabases">The driver's own answer, fetched only if the script asks.</param>
/// <param name="DriverTables">As above, for a given database.</param>
/// <param name="DriverColumns">
/// As above, for a given database/schema/table. A delegate rather than a value so that a script which
/// ignores it pays nothing — which matters for ODBC and JDBC, where the driver's own answer may not
/// work at all, and matters for the common case, where filtering the driver's list is the whole job.
/// </param>
public sealed record MetadataContext(
    DbConnection Connection,
    IScriptDialect Dialect,
    ScriptParameters Parameters,
    Func<CancellationToken, Task<IReadOnlyList<string>>> DriverDatabases,
    Func<string, CancellationToken, Task<IReadOnlyList<TableMetadata>>> DriverTables,
    Func<string, string, string, CancellationToken, Task<IReadOnlyList<ColumnMetadata>>> DriverColumns);
