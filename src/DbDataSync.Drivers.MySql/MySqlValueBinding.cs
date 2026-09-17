using System.Data.Common;
using System.Globalization;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using MySqlConnector;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against.
/// Binding a bound as text would make MySQL convert on the *column* side of the comparison, which both
/// changes the comparison's semantics and prevents an index seek — the same reason the Postgres and
/// MSSQL drivers type their bounds, restated because the type enum is the part that does not generalise.
/// </summary>
internal sealed class MySqlValueBinding : ISegmentValueBinder
{
    public static MySqlValueBinding Instance { get; } = new();

    private MySqlValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var baseType = SqlTypeName.BaseOf(column.NativeType);
        try
        {
            var (mySqlType, value) = Convert(baseType, rawValue);
            return new MySqlParameter(name, mySqlType) { Value = value };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    private static (MySqlDbType Type, object Value) Convert(string baseType, string raw) => baseType switch
    {
        "tinyint" => (MySqlDbType.Byte, sbyte.Parse(raw, CultureInfo.InvariantCulture)),
        "smallint" => (MySqlDbType.Int16, short.Parse(raw, CultureInfo.InvariantCulture)),
        "mediumint" or "int" or "integer" => (MySqlDbType.Int32, int.Parse(raw, CultureInfo.InvariantCulture)),
        "bigint" => (MySqlDbType.Int64, long.Parse(raw, CultureInfo.InvariantCulture)),
        "decimal" or "numeric" => (MySqlDbType.NewDecimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "float" => (MySqlDbType.Float, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "double" or "double precision" or "real" => (MySqlDbType.Double, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "date" => (MySqlDbType.Date, DateOnly.FromDateTime(ParseDateTime(raw))),
        "datetime" => (MySqlDbType.DateTime, ParseDateTime(raw)),
        "timestamp" => (MySqlDbType.Timestamp, ParseDateTime(raw)),
        "time" => (MySqlDbType.Time, TimeSpan.Parse(raw, CultureInfo.InvariantCulture)),
        "char" => (MySqlDbType.String, raw),
        "varchar" => (MySqlDbType.VarChar, raw),
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => (MySqlDbType.Blob, ParseBinary(raw)),
        _ => (MySqlDbType.VarChar, raw),
    };

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] ParseBinary(string raw) =>
        System.Convert.FromHexString(raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
