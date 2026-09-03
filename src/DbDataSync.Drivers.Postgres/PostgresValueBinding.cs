using System.Data.Common;
using System.Globalization;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using NpgsqlTypes;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// Converts a segment's string bound into a parameter typed to match the column it's compared against.
/// Binding a bound as text would make Postgres convert on the *column* side of the comparison, which
/// both changes the comparison's semantics and prevents an index seek — the same reason the MSSQL
/// driver types its bounds, restated because the type enum is the part that does not generalise.
/// </summary>
internal sealed class PostgresValueBinding : ISegmentValueBinder
{
    public static PostgresValueBinding Instance { get; } = new();

    private PostgresValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var baseType = SqlTypeName.BaseOf(column.NativeType);
        try
        {
            var (npgsqlType, value) = Convert(baseType, rawValue);
            return new Npgsql.NpgsqlParameter(name, npgsqlType) { Value = value };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Both the <c>information_schema</c> spellings (<c>integer</c>, <c>character varying</c>) and the
    /// internal ones (<c>int4</c>, <c>varchar</c>), because a column's type reaches here from either
    /// depending on which catalog query produced it.
    /// </summary>
    private static (NpgsqlDbType Type, object Value) Convert(string baseType, string raw) => baseType switch
    {
        "smallint" or "int2" => (NpgsqlDbType.Smallint, short.Parse(raw, CultureInfo.InvariantCulture)),
        "integer" or "int" or "int4" or "serial" => (NpgsqlDbType.Integer, int.Parse(raw, CultureInfo.InvariantCulture)),
        "bigint" or "int8" or "bigserial" => (NpgsqlDbType.Bigint, long.Parse(raw, CultureInfo.InvariantCulture)),
        "boolean" or "bool" => (NpgsqlDbType.Boolean, ParseBool(raw)),
        "numeric" or "decimal" or "money" => (NpgsqlDbType.Numeric, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "double precision" or "float8" => (NpgsqlDbType.Double, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "real" or "float4" => (NpgsqlDbType.Real, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "date" => (NpgsqlDbType.Date, DateOnly.FromDateTime(ParseDateTime(raw))),
        "timestamp" or "timestamp without time zone" => (NpgsqlDbType.Timestamp, DateTime.SpecifyKind(ParseDateTime(raw), DateTimeKind.Unspecified)),
        "timestamptz" or "timestamp with time zone" => (NpgsqlDbType.TimestampTz, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        "time" or "time without time zone" => (NpgsqlDbType.Time, TimeSpan.Parse(raw, CultureInfo.InvariantCulture)),
        "uuid" => (NpgsqlDbType.Uuid, Guid.Parse(raw)),
        "bytea" => (NpgsqlDbType.Bytea, ParseBinary(raw)),
        "character" or "bpchar" or "char" => (NpgsqlDbType.Char, raw),
        "character varying" or "varchar" => (NpgsqlDbType.Varchar, raw),
        _ => (NpgsqlDbType.Text, raw),
    };

    private static bool ParseBool(string raw) => raw switch
    {
        "1" or "t" or "true" or "TRUE" => true,
        "0" or "f" or "false" or "FALSE" => false,
        _ => bool.Parse(raw),
    };

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] ParseBinary(string raw) =>
        System.Convert.FromHexString(raw.StartsWith("\\x", StringComparison.Ordinal) ? raw[2..]
            : raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
