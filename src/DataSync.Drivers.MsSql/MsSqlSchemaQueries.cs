using System.Data.Common;
using DataSync.Drivers.Abstractions;

using DataSync.Drivers.Generic;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Shared sys.* catalog queries used by metadata introspection (<see cref="MsSqlDriver"/>), Change
/// Tracking's primary-key lookup, and staging table generation. All queries assume the caller has
/// already put <c>connection</c> into the right database context (via <c>DbConnection.ChangeDatabase</c>)
/// — they use unqualified <c>sys.*</c> names, not a database-prefixed three-part name.
/// </summary>
internal static class MsSqlSchemaQueries
{
    public static async Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, ty.name AS TypeName, c.max_length, c.precision, c.scale, c.is_nullable,
                   CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey, c.is_identity
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY c.column_id;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);

        var results = new List<ColumnMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeName = reader.GetString(1);
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            var isNullable = reader.GetBoolean(5);
            var isPrimaryKey = reader.GetInt32(6) == 1;
            var isIdentity = reader.GetBoolean(7);

            results.Add(new ColumnMetadata(
                reader.GetString(0),
                FormatSqlType(typeName, maxLength, precision, scale),
                isNullable,
                isPrimaryKey,
                isIdentity));
        }

        if (results.Count == 0)
            throw new InvalidOperationException($"Table '{schema}.{table}' was not found.");

        return results;
    }

    public static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, schema, table, cancellationToken);
        return columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
    }

    /// <summary>The bare type name from a spec produced by <see cref="FormatSqlType"/> — "decimal" from
    /// "decimal(18,2)", "int" from "int" — lowercased for switch matching.</summary>
    public static string BaseTypeName(string nativeType)
    {
        var paren = nativeType.IndexOf('(');
        return (paren < 0 ? nativeType : nativeType[..paren]).Trim().ToLowerInvariant();
    }

    /// <summary>Reconstructs a DDL-ready type spec (e.g. "nvarchar(50)", "decimal(18,2)") from the raw
    /// sys.columns length/precision/scale — a bare type name alone would default nvarchar/varchar
    /// columns to length 1 if used directly in a CREATE TABLE.</summary>
    private static string FormatSqlType(string typeName, short maxLength, byte precision, byte scale)
    {
        switch (typeName.ToLowerInvariant())
        {
            case "nchar":
            case "nvarchar":
                return maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength / 2})";
            case "char":
            case "varchar":
            case "binary":
            case "varbinary":
                return maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength})";
            case "decimal":
            case "numeric":
                return $"{typeName}({precision},{scale})";
            case "datetime2":
            case "time":
            case "datetimeoffset":
                return $"{typeName}({scale})";
            default:
                return typeName;
        }
    }
}
