using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// This driver's <see cref="ITableCatalog"/>. It uses <see cref="MsSqlSchemaQueries"/> rather than
/// <see cref="InformationSchemaQueries"/> — SQL Server has an <c>information_schema</c>, but
/// <c>sys.columns</c> is where IDENTITY lives, and a generic writer that cannot see identity columns
/// fails at apply time on exactly the tables that need the flag.
/// </summary>
internal sealed class MsSqlCatalog : ITableCatalog
{
    public static MsSqlCatalog Instance { get; } = new();

    private MsSqlCatalog() { }

    public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
        MsSqlSchemaQueries.GetColumnsAsync(connection, schema, table, cancellationToken);
}
