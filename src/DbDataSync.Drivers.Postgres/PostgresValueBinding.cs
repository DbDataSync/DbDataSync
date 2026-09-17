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
        try
        {
            // Which type the column is, and how to read a string as one, were a single switch until
            // phase 38 needed the first half on its own — see PostgresNpgsqlTypes.
            var npgsqlType = PostgresNpgsqlTypes.Of(column.NativeType);
            return new Npgsql.NpgsqlParameter(name, npgsqlType) { Value = Parse(npgsqlType, rawValue) };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The CLR value Npgsql wants for each type it was given. Keyed on the resolved
    /// <see cref="NpgsqlDbType"/> rather than on the column's type name, so the two halves cannot
    /// disagree about what <c>money</c> or <c>bigserial</c> is.
    /// </summary>
    private static object Parse(NpgsqlDbType type, string raw) => type switch
    {
        NpgsqlDbType.Smallint => short.Parse(raw, CultureInfo.InvariantCulture),
        NpgsqlDbType.Integer => int.Parse(raw, CultureInfo.InvariantCulture),
        NpgsqlDbType.Bigint => long.Parse(raw, CultureInfo.InvariantCulture),
        NpgsqlDbType.Boolean => ParseBool(raw),
        NpgsqlDbType.Numeric => decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
        NpgsqlDbType.Double => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
        NpgsqlDbType.Real => float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
        NpgsqlDbType.Date => DateOnly.FromDateTime(ParseDateTime(raw)),
        NpgsqlDbType.Timestamp => DateTime.SpecifyKind(ParseDateTime(raw), DateTimeKind.Unspecified),
        NpgsqlDbType.TimestampTz => DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        NpgsqlDbType.Time => TimeSpan.Parse(raw, CultureInfo.InvariantCulture),
        NpgsqlDbType.Uuid => Guid.Parse(raw),
        NpgsqlDbType.Bytea => ParseBinary(raw),
        _ => raw,
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
