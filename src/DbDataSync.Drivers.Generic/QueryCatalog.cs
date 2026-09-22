using System.Data.Common;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// One row of a <c>driver.yaml</c> <c>metadataQueries.tableQuery</c> result, matched to the query's
/// result columns by name (case-insensitive), not position — an operator's <c>SELECT</c> can alias,
/// reorder, or include extra columns this ignores.
/// </summary>
/// <param name="Schema">"table_schema" — required.</param>
/// <param name="Table">"table_name" — required.</param>
public sealed record QueryTableRow(string Schema, string Table);

/// <summary>
/// One row of a <c>driver.yaml</c> <c>metadataQueries.columnQuery</c> result, matched by column name —
/// see <see cref="QueryTableRow"/>. Column names deliberately match <see cref="InformationSchemaQueries"/>'s
/// own <c>SELECT</c> list (<c>column_name</c>, <c>data_type</c>, the three numeric columns, <c>is_nullable</c>)
/// for the common case — an operator adapting a near-<c>information_schema</c> view reuses names they
/// already recognise. <c>is_primary_key</c>/<c>is_identity</c> have no <c>information_schema</c>
/// precedent (primary keys need a separate join there), so they're new names chosen to read the same way.
/// </summary>
/// <param name="ColumnName">"column_name" — required.</param>
/// <param name="DataType">"data_type" — required.</param>
/// <param name="CharacterMaximumLength">"character_maximum_length" — optional, null = no length bound.</param>
/// <param name="NumericPrecision">"numeric_precision" — optional, null = not numeric or unspecified.</param>
/// <param name="NumericScale">"numeric_scale" — optional, null = not numeric or unspecified.</param>
/// <param name="IsNullable">
/// "is_nullable" — optional, "YES"/"NO". Missing or null defaults to "YES": wrongly assuming nullable
/// costs an unnecessary check or an over-permissive target; wrongly assuming NOT NULL rejects a real
/// value the source can actually produce. The safe default direction is toward nullable.
/// </param>
/// <param name="IsPrimaryKey">"is_primary_key" — optional, missing or null defaults to false.</param>
/// <param name="IsIdentity">
/// "is_identity" — optional, missing or null defaults to false — the same default
/// <see cref="InformationSchemaQueries.GetColumnsAsync"/> already hardcodes unconditionally today
/// ("standard but not universally populated... left false here"), now also offered for
/// <paramref name="IsPrimaryKey"/> when a query doesn't compute it.
/// </param>
public sealed record QueryColumnRow(
    string ColumnName,
    string DataType,
    int? CharacterMaximumLength,
    int? NumericPrecision,
    int? NumericScale,
    string? IsNullable,
    bool? IsPrimaryKey,
    bool? IsIdentity);

/// <summary>
/// The <c>driver.yaml</c> escape hatch for a vendor whose <c>DatabaseMetaData</c> (JDBC) or
/// <c>information_schema</c> answer doesn't fit DbDataSync's database/schema/table model — an operator
/// supplies raw SQL for both queries instead. <c>{{schema}}</c>/<c>{{table}}</c> in <see cref="columnQuery"/>'s
/// text are substituted with a quoted **string literal**, not an identifier — the schema/table name is
/// a value being compared against a text column (<c>WHERE table_schema = {{schema}}</c>, matching
/// <c>information_schema</c>'s own shape), not a table reference to quote. Standard SQL literal escaping
/// (embedded <c>'</c> doubled) applies uniformly, so this needs no dialect. The same raw-substitution
/// shape <c>ColumnMapping.Transform</c>'s <c>{{column}}</c> token already uses in this codebase —
/// admin-authored SQL, on the same trust footing as a source filter or a hook body, not a parameterized
/// query the operator has to know a placeholder syntax to write.
/// <para>
/// Row shapes and their required/optional fields are <see cref="QueryTableRow"/>/<see cref="QueryColumnRow"/>.
/// A required column absent from the query's result set at all fails the first time the query runs
/// against this connection, naming the missing column and which query it came from — a configuration
/// error, not a per-row one. An optional column absent from the result set, or present but <c>NULL</c>
/// on a given row, gets its documented default.
/// </para>
/// </summary>
public sealed class QueryCatalog(string tableQuery, string columnQuery) : IDescriptorCatalog
{
    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = tableQuery;

        var results = new List<TableMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var columns = new RowColumns(reader, "tableQuery");
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TableMetadata(
                columns.RequireString(reader, "table_schema"),
                columns.RequireString(reader, "table_name")));
        }
        return results;
    }

    public async Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = Substitute(columnQuery, schema, table);

        var results = new List<ColumnMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var columns = new RowColumns(reader, "columnQuery");
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new QueryColumnRow(
                ColumnName: columns.RequireString(reader, "column_name"),
                DataType: columns.RequireString(reader, "data_type"),
                CharacterMaximumLength: columns.OptionalInt(reader, "character_maximum_length"),
                NumericPrecision: columns.OptionalInt(reader, "numeric_precision"),
                NumericScale: columns.OptionalInt(reader, "numeric_scale"),
                IsNullable: columns.OptionalString(reader, "is_nullable"),
                IsPrimaryKey: columns.OptionalBool(reader, "is_primary_key"),
                IsIdentity: columns.OptionalBool(reader, "is_identity"));

            results.Add(new ColumnMetadata(
                row.ColumnName,
                InformationSchemaQueries.FormatType(row.DataType, row.CharacterMaximumLength, row.NumericPrecision, row.NumericScale),
                IsNullable: !string.Equals(row.IsNullable ?? "YES", "NO", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: row.IsPrimaryKey ?? false,
                IsIdentity: row.IsIdentity ?? false));
        }
        return results;
    }

    private static string Substitute(string query, string schema, string table) =>
        query
            .Replace("{{schema}}", Literal(schema))
            .Replace("{{table}}", Literal(table));

    /// <summary>Standard SQL string-literal escaping — embedded <c>'</c> doubled — which every engine
    /// this class could plausibly run against accepts identically, so no dialect is needed here.</summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// Resolves each named column to its ordinal once (via <see cref="DbDataReader.GetOrdinal"/>),
    /// rather than per row — and turns "the column isn't in the result set at all" into a clear,
    /// one-time configuration error for a required column, rather than a per-row exception.
    /// </summary>
    private sealed class RowColumns
    {
        private readonly string _queryName;
        private readonly Dictionary<string, int> _ordinals;

        public RowColumns(DbDataReader reader, string queryName)
        {
            _queryName = queryName;
            _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                _ordinals[reader.GetName(i)] = i;
        }

        public string RequireString(DbDataReader reader, string column)
        {
            if (!_ordinals.TryGetValue(column, out var ordinal))
                throw new InvalidOperationException(
                    $"driver.yaml's {_queryName} does not return a '{column}' column, which is required.");
            if (reader.IsDBNull(ordinal))
                throw new InvalidOperationException(
                    $"driver.yaml's {_queryName} returned NULL for '{column}', which is required.");
            return reader.GetString(ordinal);
        }

        public string? OptionalString(DbDataReader reader, string column) =>
            _ordinals.TryGetValue(column, out var ordinal) && !reader.IsDBNull(ordinal) ? reader.GetString(ordinal) : null;

        public int? OptionalInt(DbDataReader reader, string column) =>
            _ordinals.TryGetValue(column, out var ordinal) && !reader.IsDBNull(ordinal)
                ? Convert.ToInt32(reader.GetValue(ordinal))
                : null;

        public bool? OptionalBool(DbDataReader reader, string column) =>
            _ordinals.TryGetValue(column, out var ordinal) && !reader.IsDBNull(ordinal)
                ? Convert.ToBoolean(reader.GetValue(ordinal))
                : null;
    }
}
