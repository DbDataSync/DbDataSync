using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// How a driver answers "what columns does this table have". The generic pipeline needs the target's
/// column types to build a staging table and the source's to scope a segment, but catalogs are the one
/// thing that genuinely does not generalise — <c>information_schema</c> covers Postgres, MySQL and SQL
/// Server, Oracle has <c>ALL_TAB_COLUMNS</c>, and ODBC/JDBC expose provider metadata APIs instead of
/// SQL at all. So the pipeline takes one of these rather than assuming a query.
/// </summary>
public interface ITableCatalog
{
    /// <summary>The connection is already pointed at the right database (see
    /// <see cref="SqlDialect.UseDatabaseAsync"/>); this resolves schema and table only.</summary>
    Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ITableCatalog"/>, plus listing every table — what <see cref="GenericDriverSpec.Catalog"/>
/// needs (<see cref="GenericDriver.ListTablesAsync"/> calls straight through), which bare
/// <see cref="ITableCatalog"/> deliberately does not declare: not every catalog can answer it
/// (<c>MsSqlCatalog</c> has no equivalent, and gets its driver's table list a different way), so it
/// stays a separate, narrower opt-in rather than widening what every <see cref="ITableCatalog"/>
/// implementer has to provide. <see cref="InformationSchemaQueries"/> and <see cref="QueryCatalog"/> —
/// the two catalog strategies a <c>driver.yaml</c> descriptor can choose — both implement it.
/// </summary>
public interface IDescriptorCatalog : ITableCatalog
{
    Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken);
}
