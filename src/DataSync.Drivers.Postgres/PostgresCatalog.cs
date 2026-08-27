using System.Data.Common;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Postgres;

/// <summary>
/// Column metadata from <c>information_schema</c>, plus the one thing
/// <see cref="InformationSchemaQueries"/> deliberately declines to answer: whether a column is
/// generated.
/// <para>
/// Postgres populates <c>is_identity</c> and <c>identity_generation</c>, so it can be answered here
/// exactly. <c>IsIdentity</c> is set only for <c>GENERATED ALWAYS</c> — a <c>BY DEFAULT</c> identity
/// and an old-style <c>serial</c> both accept an explicit value with no override, and claiming
/// otherwise would emit an <c>OVERRIDING SYSTEM VALUE</c> the server rejects on a serial column.
/// </para>
/// </summary>
internal sealed class PostgresCatalog : ITableCatalog
{
    public static PostgresCatalog Instance { get; } = new();

    private static readonly InformationSchemaQueries Shared = new(PostgresDialect.Instance);

    private PostgresCatalog() { }

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        Shared.ListTablesAsync(connection, cancellationToken);

    public async Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = await Shared.GetColumnsAsync(connection, schema, table, cancellationToken);
        var alwaysGenerated = await GetAlwaysGeneratedAsync(connection, schema, table, cancellationToken);

        return alwaysGenerated.Count == 0
            ? columns
            : columns.Select(c => c with { IsIdentity = alwaysGenerated.Contains(c.Name) }).ToList();
    }

    private static async Task<HashSet<string>> GetAlwaysGeneratedAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND is_identity = 'YES' AND identity_generation = 'ALWAYS';
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
