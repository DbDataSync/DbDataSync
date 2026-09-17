using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// Column metadata from <c>information_schema</c>, plus the one thing
/// <see cref="InformationSchemaQueries"/> deliberately declines to answer: whether a column is
/// generated. MySQL sets <c>information_schema.columns.extra</c> to <c>auto_increment</c> for one, so
/// it is answered here exactly, the same shape <c>PostgresCatalog</c> uses for its own identity check.
/// <para>
/// <see cref="ListTablesAsync"/> is a full override, not a pass-through, for a reason specific to
/// MySQL: <see cref="InformationSchemaQueries.ListTablesAsync"/> has no <c>table_schema</c> filter,
/// which is correct for Postgres (whose <c>information_schema</c> is already scoped to the connected
/// database and cannot see another one) but wrong for MySQL, whose <c>information_schema.tables</c> is
/// server-wide — every database on the instance, not just the one <c>UseDatabaseAsync</c> just
/// switched to. Left unfiltered, this would return every table on the server. <c>DATABASE()</c> is
/// MySQL's own "which database is this session currently in" function, and is exactly what
/// <c>UseDatabaseAsync</c>'s <c>USE</c> statement just set.
/// </para>
/// </summary>
internal sealed class MySqlCatalog : ITableCatalog
{
    public static MySqlCatalog Instance { get; } = new();

    private static readonly InformationSchemaQueries Shared = new(MySqlDialect.Instance);

    private MySqlCatalog() { }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE' AND table_schema = DATABASE()
            ORDER BY table_schema, table_name;
            """;

        var results = new List<TableMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TableMetadata(reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public async Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = await Shared.GetColumnsAsync(connection, schema, table, cancellationToken);
        var autoIncrement = await GetAutoIncrementColumnsAsync(connection, schema, table, cancellationToken);

        return autoIncrement.Count == 0
            ? columns
            : columns.Select(c => c with { IsIdentity = autoIncrement.Contains(c.Name) }).ToList();
    }

    private static async Task<HashSet<string>> GetAutoIncrementColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table AND extra LIKE '%auto_increment%';
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }
}
