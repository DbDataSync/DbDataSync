using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DuckDB.NET.Data;

namespace DbDataSync.Drivers.DuckDb;

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against
/// — the same reasoning every other driver's own <see cref="ISegmentValueBinder"/> restates: binding a
/// bound as an untyped string would make the engine convert on the *column* side of the comparison,
/// which both changes the comparison's semantics and prevents an index seek.
/// <para>
/// <c>column.NativeType</c> here is never a live catalog answer — <see cref="DuckDbDriver.ListColumnsAsync"/>
/// deliberately returns nothing (a DuckDB source is a query, not a table with a catalog entry). It's
/// always what a query preview's own <c>DbDataReader.GetColumnSchema()</c> reported and the mapping
/// cached — DuckDB's own native type names (<c>INTEGER</c>, <c>DOUBLE</c>, <c>TIMESTAMP</c>, …).
/// </para>
/// <para>
/// <c>DuckDBParameter</c> takes the standard ADO.NET <see cref="DbType"/> (not a DuckDB-specific enum),
/// so this only needs to parse the right CLR value — DuckDB.NET.Data infers the native binding from it.
/// </para>
/// </summary>
internal sealed class DuckDbValueBinding : ISegmentValueBinder
{
    public static DuckDbValueBinding Instance { get; } = new();

    private DuckDbValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var baseType = column.NativeType.Split('(', 2)[0].Trim().ToUpperInvariant();
        try
        {
            var (dbType, value) = Convert(baseType, rawValue);
            var parameter = new DuckDBParameter(name, dbType, value);
            if (dbType == DbType.Decimal && value is decimal d)
            {
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

    private static (DbType Type, object Value) Convert(string baseType, string raw) => baseType switch
    {
        "TINYINT" or "INT1" => (DbType.SByte, sbyte.Parse(raw, CultureInfo.InvariantCulture)),
        "SMALLINT" or "INT2" or "SHORT" => (DbType.Int16, short.Parse(raw, CultureInfo.InvariantCulture)),
        "INTEGER" or "INT4" or "INT" or "SIGNED" => (DbType.Int32, int.Parse(raw, CultureInfo.InvariantCulture)),
        "BIGINT" or "INT8" or "LONG" => (DbType.Int64, long.Parse(raw, CultureInfo.InvariantCulture)),
        "UTINYINT" => (DbType.Byte, byte.Parse(raw, CultureInfo.InvariantCulture)),
        "USMALLINT" => (DbType.UInt16, ushort.Parse(raw, CultureInfo.InvariantCulture)),
        "UINTEGER" => (DbType.UInt32, uint.Parse(raw, CultureInfo.InvariantCulture)),
        "UBIGINT" => (DbType.UInt64, ulong.Parse(raw, CultureInfo.InvariantCulture)),
        "BOOLEAN" or "BOOL" or "LOGICAL" => (DbType.Boolean, ParseBool(raw)),
        "DECIMAL" or "NUMERIC" => (DbType.Decimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "FLOAT" or "REAL" or "FLOAT4" => (DbType.Single, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "DOUBLE" or "FLOAT8" => (DbType.Double, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "DATE" => (DbType.Date, DateOnly.Parse(raw, CultureInfo.InvariantCulture)),
        "TIME" => (DbType.Time, TimeSpan.Parse(raw, CultureInfo.InvariantCulture)),
        "TIMESTAMP" or "DATETIME" or "TIMESTAMP_NS" or "TIMESTAMP_MS" or "TIMESTAMP_S" =>
            (DbType.DateTime, ParseDateTime(raw)),
        "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" =>
            (DbType.DateTimeOffset, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        "UUID" => (DbType.Guid, Guid.Parse(raw)),
        "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" => (DbType.Binary, ParseBinary(raw)),
        _ => (DbType.String, raw),
    };

    private static byte ScaleOf(decimal value) => (byte)((decimal.GetBits(value)[3] >> 16) & 0xFF);

    private static bool ParseBool(string raw) => raw switch
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
