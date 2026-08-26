using System.Data;
using System.Data.Common;
using System.Globalization;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// A rendered <see cref="BatchReloadSegment"/>: the SQL predicate limiting a statement to that
/// segment's rows, plus the parameters it references. Columns are bracket-quoted and every bound value
/// is a typed parameter — never spliced into the text.
/// </summary>
internal sealed record MsSqlSegmentScope(string Predicate, IReadOnlyList<SqlParameter> Parameters)
{
    /// <summary>Matches every row. Used for <see cref="FullSegment"/> and for an unsegmented unit of
    /// work, so callers can always interpolate a predicate rather than conditionally emitting the
    /// whole WHERE clause.</summary>
    public static MsSqlSegmentScope All { get; } = new("1 = 1", []);

    public void AddTo(DbCommand command)
    {
        foreach (var parameter in Parameters)
            command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Renders <paramref name="segment"/> against the table described by <paramref name="columns"/>
    /// (the source table for a reader, the target table for a writer — the segment column must exist
    /// on whichever side is being scoped).
    /// <para>
    /// Bound values arrive as strings from a web form or from system-computed bucket boundaries, so
    /// they're converted to the segment column's actual CLR/SQL type and bound as parameters. This is
    /// deliberately stricter than <c>SourceTableRef.Filter</c>, which is a raw predicate an
    /// administrator hand-authors in config; segment bounds are ordinary user input.
    /// </para>
    /// </summary>
    /// <param name="columnMappings">
    /// Supplied by writers, omitted by readers. A segment names a column on the *source* table, but a
    /// writer scopes the *target* — and a mapping is free to rename a column across the two. Passing
    /// the mappings translates the segment's column name to its target-side counterpart, so a reload
    /// segmented on a renamed column scopes the same rows on both sides instead of failing to find the
    /// column (or, worse, finding an unrelated target column that happens to share the source name).
    /// </param>
    public static MsSqlSegmentScope Build(
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings = null) =>
        segment switch
        {
            null or FullSegment => All,
            ListSegment list => BuildList(list, ResolveColumn(list.Column, columns, columnMappings)),
            RangeSegment range => BuildRange(range, ResolveColumn(range.Column, columns, columnMappings)),
            AutoSegment auto => throw new InvalidOperationException(
                $"Auto segment on '{auto.Column}' reached execution unexpanded. Auto segments must be " +
                "expanded into concrete ranges (ISegmentExpandingReader.ExpandAutoSegmentsAsync) when " +
                "the work is enqueued, never carried through as a runtime segment."),
            _ => throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown segment mode."),
        };

    private static MsSqlSegmentScope BuildList(ListSegment list, ColumnMetadata column)
    {
        if (list.Values.Count == 0)
            throw new InvalidOperationException(
                $"List segment on '{list.Column}' has no values. An empty list matches nothing, which " +
                "for a reconciling writer would delete the target's entire scope — reject it rather " +
                "than render 'IN ()' (which isn't valid SQL anyway).");

        var parameters = list.Values
            .Select((value, i) => MsSqlValueBinding.CreateParameter($"@__seg{i}", value, column))
            .ToList();

        var placeholders = string.Join(", ", parameters.Select(p => p.ParameterName));
        return new MsSqlSegmentScope($"{SqlIdentifier.Quote(column.Name)} IN ({placeholders})", parameters);
    }

    private static MsSqlSegmentScope BuildRange(RangeSegment range, ColumnMetadata column)
    {
        var quoted = SqlIdentifier.Quote(column.Name);
        // Half-open: consecutive ranges tile a value space with no gap and no row processed twice.
        return new MsSqlSegmentScope(
            $"{quoted} >= @__segMin AND {quoted} < @__segMax",
            [
                MsSqlValueBinding.CreateParameter("@__segMin", range.RangeMin, column),
                MsSqlValueBinding.CreateParameter("@__segMax", range.RangeMax, column),
            ]);
    }

    private static ColumnMetadata ResolveColumn(
        string columnName, IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<ColumnMapping>? columnMappings)
    {
        var resolvedName = columnMappings?
            .FirstOrDefault(m => string.Equals(m.SourceColumn, columnName, StringComparison.OrdinalIgnoreCase))?
            .TargetColumn ?? columnName;

        return columns.FirstOrDefault(c => string.Equals(c.Name, resolvedName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Segment column '{resolvedName}' was not found (available: {string.Join(", ", columns.Select(c => c.Name))}).");
    }
}

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against.
/// Binding a bound as an untyped string would make SQL Server convert on the *column* side of the
/// comparison for anything non-textual, which both changes the comparison's semantics and prevents an
/// index seek — the opposite of what segment scoping exists to achieve.
/// </summary>
internal static class MsSqlValueBinding
{
    public static SqlParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var baseType = MsSqlSchemaQueries.BaseTypeName(column.NativeType);
        try
        {
            var (sqlDbType, value) = Convert(baseType, rawValue);
            var parameter = new SqlParameter(name, sqlDbType) { Value = value };
            if (sqlDbType == SqlDbType.Decimal && value is decimal d)
            {
                // Precision/scale must be set explicitly. A SqlDbType.Decimal parameter left at the
                // default scale of 0 silently truncates the fractional part of the bound, which turns
                // a range boundary into a slightly wrong one rather than into an error.
                parameter.Precision = 38;
                parameter.Scale = ScaleOf(d);
            }
            return parameter;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    private static (SqlDbType Type, object Value) Convert(string baseType, string raw) => baseType switch
    {
        "tinyint" => (SqlDbType.TinyInt, byte.Parse(raw, CultureInfo.InvariantCulture)),
        "smallint" => (SqlDbType.SmallInt, short.Parse(raw, CultureInfo.InvariantCulture)),
        "int" => (SqlDbType.Int, int.Parse(raw, CultureInfo.InvariantCulture)),
        "bigint" => (SqlDbType.BigInt, long.Parse(raw, CultureInfo.InvariantCulture)),
        "bit" => (SqlDbType.Bit, ParseBit(raw)),
        "decimal" or "numeric" => (SqlDbType.Decimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "money" => (SqlDbType.Money, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "smallmoney" => (SqlDbType.SmallMoney, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "float" => (SqlDbType.Float, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "real" => (SqlDbType.Real, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "date" => (SqlDbType.Date, ParseDateTime(raw)),
        "datetime" => (SqlDbType.DateTime, ParseDateTime(raw)),
        "smalldatetime" => (SqlDbType.SmallDateTime, ParseDateTime(raw)),
        "datetime2" => (SqlDbType.DateTime2, ParseDateTime(raw)),
        "datetimeoffset" => (SqlDbType.DateTimeOffset, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        "time" => (SqlDbType.Time, TimeSpan.Parse(raw, CultureInfo.InvariantCulture)),
        "uniqueidentifier" => (SqlDbType.UniqueIdentifier, Guid.Parse(raw)),
        "binary" or "varbinary" => (SqlDbType.VarBinary, ParseBinary(raw)),
        "char" => (SqlDbType.Char, raw),
        "varchar" => (SqlDbType.VarChar, raw),
        "text" => (SqlDbType.VarChar, raw),
        "nchar" => (SqlDbType.NChar, raw),
        "nvarchar" => (SqlDbType.NVarChar, raw),
        "ntext" => (SqlDbType.NVarChar, raw),
        _ => (SqlDbType.NVarChar, raw),
    };

    /// <summary>The number of digits after the decimal point the value actually carries — bits[3]'s
    /// second byte is where <see cref="decimal"/> stores its scale.</summary>
    private static byte ScaleOf(decimal value) => (byte)((decimal.GetBits(value)[3] >> 16) & 0xFF);

    private static bool ParseBit(string raw) => raw switch
    {
        "1" => true,
        "0" => false,
        _ => bool.Parse(raw),
    };

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] ParseBinary(string raw) =>
        System.Convert.FromHexString(raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
