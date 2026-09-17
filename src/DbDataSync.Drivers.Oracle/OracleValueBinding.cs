using System.Data.Common;
using System.Globalization;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against.
/// Binding a bound as text would make Oracle convert on the *column* side of the comparison, which both
/// changes the comparison's semantics and prevents an index seek — the same reason every other driver's
/// value binder exists, restated because the type enum is the part that does not generalise.
/// <para>
/// Oracle has exactly one numeric column family (<c>NUMBER</c>), unlike Postgres/MySQL's separate
/// smallint/integer/bigint/decimal types — so every numeric bound here binds as
/// <see cref="OracleDbType.Decimal"/> uniformly rather than picking a narrower type by precision, which
/// is simpler and correct rather than a shortcut: Oracle's own engine does not distinguish either.
/// </para>
/// </summary>
internal sealed class OracleValueBinding : ISegmentValueBinder
{
    public static OracleValueBinding Instance { get; } = new();

    private OracleValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        try
        {
            var (oracleType, value) = Convert(column.NativeType, rawValue);
            return new OracleParameter(name, oracleType) { Value = value };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checked against the full native type string, not <see cref="SqlTypeName.BaseOf"/>'s truncated
    /// name — the same reason <c>OracleDialect.ToCanonicalType</c> checks the raw string for
    /// <c>WITH TIME ZONE</c> before falling through to the shared parser: a plain
    /// <c>SqlTypeName.BaseOf("TIMESTAMP(6) WITH TIME ZONE")</c> would come back <c>"timestamp"</c>,
    /// indistinguishable from a zone-naive column, and bind a zoned bound as the wrong .NET type.
    /// </summary>
    private static (OracleDbType Type, object Value) Convert(string nativeType, string raw)
    {
        var upper = nativeType.Trim().ToUpperInvariant();

        if (upper.Contains("WITH TIME ZONE"))
            return (OracleDbType.TimeStampTZ, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        var baseType = SqlTypeName.BaseOf(upper);
        return baseType switch
        {
            "number" => (OracleDbType.Decimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "binary_float" => (OracleDbType.BinaryFloat, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "binary_double" => (OracleDbType.BinaryDouble, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "date" => (OracleDbType.Date, ParseDateTime(raw)),
            "timestamp" => (OracleDbType.TimeStamp, ParseDateTime(raw)),
            "char" or "nchar" => (OracleDbType.Char, raw),
            "varchar2" or "nvarchar2" => (OracleDbType.Varchar2, raw),
            "raw" => (OracleDbType.Raw, ParseBinary(raw)),
            _ => (OracleDbType.Varchar2, raw),
        };
    }

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] ParseBinary(string raw) =>
        System.Convert.FromHexString(raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
