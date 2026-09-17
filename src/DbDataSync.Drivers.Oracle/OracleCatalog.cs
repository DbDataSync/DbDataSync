using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Built from scratch against Oracle's own data dictionary — Oracle has no <c>information_schema</c>,
/// so unlike <c>MySqlCatalog</c> (phase 147) there is no <c>InformationSchemaQueries</c> to wrap. This
/// is the one place phase 148 genuinely costs more than phase 147 did, confirmed rather than assumed.
/// <para>
/// A schema/owner filter is required everywhere here, the same role <c>table_schema</c> plays for
/// MySQL — Oracle's dictionary views (<c>ALL_TAB_COLUMNS</c>, <c>ALL_TABLES</c>) are instance-wide, not
/// scoped to a single "current database" the way Postgres's are.
/// </para>
/// <para>
/// Every catalog query and every type-name string this class builds was checked against a live Oracle
/// 23ai instance, not assumed: <c>TIMESTAMP</c>'s <c>DATA_TYPE</c> already carries its own precision
/// (<c>"TIMESTAMP(6)"</c>, not bare <c>"TIMESTAMP"</c> with a separate scale to append — a real,
/// non-obvious per-type-family inconsistency in <c>ALL_TAB_COLUMNS</c>), <c>CHAR_LENGTH</c> (not
/// <c>DATA_LENGTH</c>, which is byte length and reports meaninglessly for a LOB) is the right figure
/// for a character type, and <c>ALL_USERS.ORACLE_MAINTAINED</c> — a column requiring 12c+, which this
/// driver's own floor already assumes — is what keeps <see cref="ListTablesAsync"/> from flooding an
/// operator's table picker with Oracle's own SYS/SYSTEM/CTXSYS/... schemas the way an unfiltered query
/// would.
/// </para>
/// <para>
/// **Every bind variable here is named <c>tableName</c>, never <c>table</c>.** Confirmed the hard way:
/// Oracle rejects a handful of reserved words as bind-variable names outright (<c>ORA-01745</c>) even
/// though the identical word is perfectly fine as a quoted column or table identifier — <c>table</c> and
/// <c>trigger</c> are both on that list, <c>schema</c> and <c>name</c> are not. Not documented anywhere
/// obvious; found by a query that had worked in every other position suddenly refusing to parse.
/// </para>
/// </summary>
internal sealed class OracleCatalog : ITableCatalog
{
    public static OracleCatalog Instance { get; } = new();

    private OracleCatalog() { }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT t.owner, t.table_name
            FROM all_tables t
            JOIN all_users u ON u.username = t.owner
            WHERE u.oracle_maintained = 'N'
            ORDER BY t.owner, t.table_name
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
        var identity = await GetIdentityColumnsAsync(connection, schema, table, cancellationToken);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT column_name, data_type, char_length, data_length, data_precision, data_scale, nullable
            FROM all_tab_columns
            WHERE owner = :schema AND table_name = :tableName
            ORDER BY column_id
            """;
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("tableName", table.ToUpperInvariant());

        var results = new List<ColumnMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var nativeType = FormatType(
                reader.GetString(1),
                Nullable(reader, 2),
                Nullable(reader, 3),
                NullableDecimal(reader, 4),
                NullableDecimal(reader, 5));

            results.Add(new ColumnMetadata(
                name,
                nativeType,
                IsNullable: reader.GetString(6).Equals("Y", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: primaryKey.Contains(name),
                IsIdentity: identity.Contains(name)));
        }
        return results;
    }

    private static async Task<HashSet<string>> GetPrimaryKeyAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT cc.column_name
            FROM all_constraints c
            JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
            WHERE c.owner = :schema AND c.table_name = :tableName AND c.constraint_type = 'P'
            """;
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("tableName", table.ToUpperInvariant());

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>
    /// <c>ALL_TAB_IDENTITY_COLS</c> — 12c+, matching this driver's own version floor (see
    /// <c>OracleTriggerAudit</c>). Both <c>ALWAYS</c> and <c>BY DEFAULT</c>/<c>BY DEFAULT ON NULL</c>
    /// generation types are reported here as identity; the distinction between them only matters at
    /// write time (see <c>OracleDialect.WriteWithGeneratedColumnOverrideAsync</c>'s own doc comment),
    /// not for a catalog read.
    /// </summary>
    private static async Task<HashSet<string>> GetIdentityColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT column_name FROM all_tab_identity_cols WHERE owner = :schema AND table_name = :tableName";
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("tableName", table.ToUpperInvariant());

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    private static int? Nullable(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));

    private static decimal? NullableDecimal(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));

    /// <summary>
    /// Reassembles a DDL-ready spec from <c>ALL_TAB_COLUMNS</c>'s own, genuinely inconsistent shape —
    /// confirmed against a live server, not assumed. <c>TIMESTAMP</c>'s <c>DATA_TYPE</c> already
    /// includes its precision (and, for a zoned column, its <c>WITH [LOCAL] TIME ZONE</c> suffix); every
    /// other type family reports a bare name and needs its length/precision appended from the separate
    /// columns this method was given.
    /// </summary>
    private static string FormatType(
        string dataType, int? charLength, int? dataLength, decimal? precision, decimal? scale)
    {
        var upper = dataType.Trim().ToUpperInvariant();

        if (upper.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            return upper;

        return upper switch
        {
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" => $"{upper}({charLength ?? 1})",
            "NUMBER" => precision is null ? "NUMBER" : $"NUMBER({(int)precision},{(int)(scale ?? 0)})",
            "RAW" => $"RAW({dataLength ?? 1})",
            // DATE, CLOB, NCLOB, BLOB, LONG RAW, XMLTYPE and everything else report bare and stay bare.
            _ => upper,
        };
    }
}
