using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Catalog lookups over <c>information_schema</c>, which Postgres, MySQL and SQL Server all provide.
/// <para>
/// A **helper a driver may use**, not part of the generic contract — which is why it implements
/// <see cref="ITableCatalog"/> rather than being assumed by the pipeline. Oracle has no
/// <c>information_schema</c> and supplies its own; ODBC and JDBC expose provider metadata APIs instead
/// of SQL. A driver whose engine does have one saves writing this; the rest are unaffected.
/// </para>
/// <para>
/// Primary keys come from the <c>table_constraints</c>/<c>key_column_usage</c> pair. Generated columns
/// do not: <c>is_identity</c> is standard but not universally populated, so
/// <see cref="ColumnMetadata.IsIdentity"/> is left false here and a driver that can detect it should
/// override with its own catalog query rather than let a wrong answer through.
/// </para>
/// </summary>
public sealed class InformationSchemaQueries(SqlDialect dialect) : ITableCatalog
{
    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
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
        var primaryKey = await GetPrimaryKeyAsync(connection, schema, table, cancellationToken);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = $"""
            SELECT column_name, data_type, character_maximum_length, numeric_precision, numeric_scale, is_nullable
            FROM information_schema.columns
            WHERE table_schema = {dialect.ParameterReference("schema")} AND table_name = {dialect.ParameterReference("table")}
            ORDER BY ordinal_position;
            """;
        cmd.AddParameter(dialect.ParameterName("schema"), schema);
        cmd.AddParameter(dialect.ParameterName("table"), table);

        var results = new List<ColumnMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            results.Add(new ColumnMetadata(
                name,
                FormatType(reader.GetString(1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4)),
                IsNullable: reader.GetString(5).Equals("YES", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: primaryKey.Contains(name),
                // Standard but unevenly populated; a driver that can detect it does so itself rather
                // than have a wrong answer default in from here.
                IsIdentity: false));
        }
        return results;
    }

    private async Task<HashSet<string>> GetPrimaryKeyAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = $"""
            SELECT k.column_name
            FROM information_schema.table_constraints c
            JOIN information_schema.key_column_usage k
              ON k.constraint_name = c.constraint_name
             AND k.table_schema = c.table_schema
             AND k.table_name = c.table_name
            WHERE c.constraint_type = 'PRIMARY KEY'
              AND c.table_schema = {dialect.ParameterReference("schema")}
              AND c.table_name = {dialect.ParameterReference("table")};
            """;
        cmd.AddParameter(dialect.ParameterName("schema"), schema);
        cmd.AddParameter(dialect.ParameterName("table"), table);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    private static int? Nullable(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));

    /// <summary>Reassembles a DDL-ready spec. A bare type name would default a varchar column to
    /// length 1 and a decimal to scale 0 if used in a CREATE TABLE, which is exactly what the staging
    /// provider does with it.
    /// <para>
    /// <c>public</c> rather than <c>private</c> — phase 167V — so <c>DbDataSync.Drivers.Jdbc</c>'s
    /// <c>DatabaseMetaData</c>-based catalog can reuse the same length/precision/scale assembly rather
    /// than reimplementing it: <c>java.sql.DatabaseMetaData.getColumns()</c>'s single <c>COLUMN_SIZE</c>
    /// means either a length or a precision depending on the SQL type, unlike
    /// <c>information_schema</c>'s separate columns, so the caller picks which one to pass and this
    /// method's own logic is unchanged. `InformationSchemaQueries` is already a public class in this
    /// public API surface, so this follows that, rather than adding `InternalsVisibleTo` for one method.
    /// </para>
    /// </summary>
    public static string FormatType(string dataType, int? maxLength, int? precision, int? scale)
    {
        var lower = dataType.ToLowerInvariant();
        if (maxLength is int length)
            return length < 0 ? $"{lower}(max)" : $"{lower}({length})";
        if (precision is int p && lower is "decimal" or "numeric")
            return $"{lower}({p},{scale ?? 0})";
        return lower;
    }
}
