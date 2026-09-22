using System.Data.Common;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Jdbc.Imported;

namespace DbDataSync.Drivers.Jdbc;

/// <summary>
/// Backed by <c>java.sql.DatabaseMetaData</c> — phase 167V. Standard across JDBC engines, so this is the
/// default answer for any JDBC-backed connection, rather than phase 165V's original choice
/// (<see cref="InformationSchemaQueries"/>, reused unmodified), which only ever worked because its one
/// target was Postgres.
/// <para>
/// Still not a guarantee of the right answer for every vendor — <c>DatabaseMetaData</c>'s own
/// catalog/schema semantics vary by engine (Oracle's "database" is a service/schema; MySQL conflates
/// schema and database), which this class does not try to resolve centrally. The escape hatches for that
/// live elsewhere: a <c>driver.yaml</c> descriptor's query-based catalog strategy
/// (<c>metadataQueries.tableQuery</c>/<c>columnQuery</c>), and the <c>metadataProvider</c> script
/// override. See <c>architecture/planning/todo/jdbc-metadata-catalog.md</c>.
/// </para>
/// </summary>
internal sealed class JdbcCatalog : IDescriptorCatalog
{
    public static JdbcCatalog Instance { get; } = new();

    private JdbcCatalog() { }

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var jdbc = Require(connection);
        var metadata = jdbc.JavaSqlConnection.getMetaData();
        var catalog = jdbc.JavaSqlConnection.getCatalog();

        var results = new List<TableMetadata>();
        var resultSet = metadata.getTables(catalog, null, "%", new[] { "TABLE" });
        try
        {
            while (resultSet.next())
                results.Add(new TableMetadata(
                    resultSet.getString("TABLE_SCHEM") ?? "",
                    resultSet.getString("TABLE_NAME")));
        }
        finally
        {
            resultSet.close();
        }

        return Task.FromResult<IReadOnlyList<TableMetadata>>(results);
    }

    public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var jdbc = Require(connection);
        var metadata = jdbc.JavaSqlConnection.getMetaData();
        var catalog = jdbc.JavaSqlConnection.getCatalog();

        var primaryKey = ReadPrimaryKeyColumns(metadata, catalog, schema, table);

        var results = new List<ColumnMetadata>();
        var resultSet = metadata.getColumns(catalog, schema, table, "%");
        try
        {
            while (resultSet.next())
            {
                var name = resultSet.getString("COLUMN_NAME");
                var sqlType = resultSet.getInt("DATA_TYPE");
                var typeName = resultSet.getString("TYPE_NAME");
                var columnSize = NullableInt(resultSet, "COLUMN_SIZE");
                var decimalDigits = NullableInt(resultSet, "DECIMAL_DIGITS");

                // "YES" / "NO" / "" (unknown). Missing or unknown defaults toward nullable — wrongly
                // assuming NOT NULL rejects a real value the source can actually produce; wrongly
                // assuming nullable costs at most an unnecessary check.
                var isNullable = TryGetString(resultSet, "IS_NULLABLE");

                // JDBC 4.0+ column, not guaranteed present on every driver's ResultSet — same "standard
                // but not universally populated" situation InformationSchemaQueries already documents
                // for IsIdentity, so the same false-when-unknown default applies.
                var autoIncrement = TryGetString(resultSet, "IS_AUTOINCREMENT");

                results.Add(new ColumnMetadata(
                    name,
                    FormatNativeType(typeName, sqlType, columnSize, decimalDigits),
                    IsNullable: !string.Equals(isNullable, "NO", StringComparison.OrdinalIgnoreCase),
                    IsPrimaryKey: primaryKey.Contains(name),
                    IsIdentity: string.Equals(autoIncrement, "YES", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            resultSet.close();
        }

        return Task.FromResult<IReadOnlyList<ColumnMetadata>>(results);
    }

    private static HashSet<string> ReadPrimaryKeyColumns(
        java.sql.DatabaseMetaData metadata, string? catalog, string schema, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultSet = metadata.getPrimaryKeys(catalog, schema, table);
        try
        {
            while (resultSet.next())
                names.Add(resultSet.getString("COLUMN_NAME"));
        }
        finally
        {
            resultSet.close();
        }
        return names;
    }

    /// <summary>
    /// <c>java.sql.DatabaseMetaData.getColumns()</c>'s single <c>COLUMN_SIZE</c> means either a
    /// character/binary length or a numeric precision, depending on the SQL type — unlike
    /// <c>information_schema</c>, which reports them as separate columns. This picks the right one out
    /// and hands off to <see cref="InformationSchemaQueries.FormatType"/> for the actual assembly, rather
    /// than reimplementing it.
    /// </summary>
    private static string FormatNativeType(string typeName, int sqlType, int? columnSize, int? decimalDigits) =>
        IsCharacterOrBinary(sqlType) ? InformationSchemaQueries.FormatType(typeName, columnSize, null, null)
        : IsExactNumeric(sqlType) ? InformationSchemaQueries.FormatType(typeName, null, columnSize, decimalDigits)
        : InformationSchemaQueries.FormatType(typeName, null, null, null);

    private static bool IsCharacterOrBinary(int sqlType) => sqlType switch
    {
        java.sql.Types.CHAR or java.sql.Types.VARCHAR or java.sql.Types.LONGVARCHAR
            or java.sql.Types.NCHAR or java.sql.Types.NVARCHAR or java.sql.Types.LONGNVARCHAR
            or java.sql.Types.BINARY or java.sql.Types.VARBINARY or java.sql.Types.LONGVARBINARY => true,
        _ => false,
    };

    private static bool IsExactNumeric(int sqlType) =>
        sqlType is java.sql.Types.DECIMAL or java.sql.Types.NUMERIC;

    private static JdbcConnection Require(DbConnection connection) =>
        connection as JdbcConnection
        ?? throw new InvalidOperationException(
            $"JdbcCatalog requires a JdbcConnection, got '{connection.GetType()}'.");

    private static int? NullableInt(java.sql.ResultSet resultSet, string column)
    {
        var value = resultSet.getInt(column);
        return resultSet.wasNull() ? null : value;
    }

    /// <summary>Some result-set columns (notably <c>IS_AUTOINCREMENT</c>, a JDBC 4.0 addition) are not
    /// guaranteed present on every driver's <c>getColumns()</c> answer. Absent reads as unknown, the same
    /// as a present-but-empty value — both default the same way at the call site.</summary>
    private static string? TryGetString(java.sql.ResultSet resultSet, string column)
    {
        try
        {
            return resultSet.getString(column);
        }
        catch (java.sql.SQLException)
        {
            return null;
        }
    }
}
