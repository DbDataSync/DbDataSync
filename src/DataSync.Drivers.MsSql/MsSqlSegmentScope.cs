using System.Data;
using System.Data.Common;
using System.Globalization;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using DataSync.Core.Sql;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// This driver's binding of the engine-neutral <see cref="SegmentScope"/>: its dialect for quoting and
/// placeholders, its value binder for typed bounds. Predicate rendering itself lives in
/// <see cref="SegmentScope"/> — there is nothing SQL Server-specific about <c>IN (…)</c> or a
/// half-open range, and duplicating it per engine is what this layer exists to avoid.
/// </summary>
internal static class MsSqlSegmentScope
{
    public static SegmentScope Build(
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings = null) =>
        SegmentScope.Build(MsSqlDialect.Instance, MsSqlValueBinding.Instance, segment, columns, columnMappings);
}

/// <summary>
/// Converts a segment's string bound into a SQL Server parameter typed to match the column it's
/// compared against.
/// Binding a bound as an untyped string would make SQL Server convert on the *column* side of the
/// comparison for anything non-textual, which both changes the comparison's semantics and prevents an
/// index seek — the opposite of what segment scoping exists to achieve.
/// </summary>
internal sealed class MsSqlValueBinding : ISegmentValueBinder
{
    public static MsSqlValueBinding Instance { get; } = new();

    private MsSqlValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
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
