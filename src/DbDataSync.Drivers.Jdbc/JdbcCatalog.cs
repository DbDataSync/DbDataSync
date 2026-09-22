using System.Data.Common;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// <see cref="InformationSchemaQueries"/>, unmodified — the same helper <c>PostgresCatalog</c>/
/// <c>MySqlCatalog</c> use, run as plain SQL over the JDBC connection.
/// <para>
/// <see cref="InformationSchemaQueries"/>'s own doc comment says ODBC and JDBC "expose provider metadata
/// APIs instead of SQL", which is true in general — a JDBC-backed engine with no
/// <c>information_schema</c> (Oracle, say) would need its own catalog the way <c>OracleCatalog</c> does.
/// But phase 165V's driver targets exactly one engine, Postgres, and Postgres's
/// <c>information_schema</c> is ordinary SQL regardless of which driver carries the bytes there — so
/// reusing this rather than writing a <c>DatabaseMetaData</c>-based catalog is the honest choice for what
/// this phase actually needs, not a shortcut. A real cross-vendor JDBC catalog (the planning doc's own
/// still-open "metadata browsing shape" question) is exactly the kind of thing that would need
/// <c>DatabaseMetaData</c> instead, and is out of scope here.
/// </para>
/// </summary>
internal sealed class JdbcCatalog : ITableCatalog
{
    public static JdbcCatalog Instance { get; } = new();

    private static readonly InformationSchemaQueries Shared = new(JdbcDialect.Instance);

    private JdbcCatalog() { }

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Shared.ListTablesAsync(connection, cancellationToken);

    public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
        Shared.GetColumnsAsync(connection, schema, table, cancellationToken);
}
