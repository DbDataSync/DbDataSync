using System.Data.Common;
using DataSync.Drivers.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Generic;

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
