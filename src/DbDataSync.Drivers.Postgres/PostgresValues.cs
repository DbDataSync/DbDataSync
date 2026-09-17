using System.Globalization;
using NpgsqlTypes;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// Reads a Postgres value out of its text form, as the CLR type the column's own type implies.
/// <para>
/// Two callers, for two unrelated reasons. <see cref="PostgresValueBinding"/> turns an operator's
/// segment bound — always text, because that is what config holds — into a typed parameter.
/// <see cref="PgLogicalSlotReader"/> turns a decoded WAL change into a change row: <c>wal2json</c>
/// emits JSON, which has three scalar types where Postgres has a hundred, so every value arrives as a
/// string, a number or a bool and has to be read back as what the column actually is.
/// </para>
/// <para>
/// Keyed on the resolved <see cref="NpgsqlDbType"/> rather than on the type name, so this and
/// <see cref="PostgresNpgsqlTypes"/> cannot disagree about what <c>money</c> or <c>bigserial</c> is.
/// A type neither of them knows reads back as text, which is what the value already was.
/// </para>
/// </summary>
internal static class PostgresValues
{
    /// <summary>Throws <see cref="FormatException"/>, <see cref="OverflowException"/> or
    /// <see cref="ArgumentException"/> for text that is not a value of that type — callers wrap those
    /// with whatever context they have, which is the part a bare parse error lacks.</summary>
    public static object FromText(string nativeType, string text) =>
        FromText(PostgresNpgsqlTypes.Of(nativeType), text);

    public static object FromText(NpgsqlDbType type, string raw) => type switch
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

    /// <summary>Postgres writes a boolean as <c>t</c>/<c>f</c>, JSON writes it as <c>true</c>/<c>false</c>,
    /// and an operator typing a segment bound writes whichever they think of first.</summary>
    private static bool ParseBool(string raw) => raw switch
    {
        "1" or "t" or "true" or "TRUE" => true,
        "0" or "f" or "false" or "FALSE" => false,
        _ => bool.Parse(raw),
    };

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary><c>\x</c>-prefixed is Postgres's own hex output (and <c>wal2json</c>'s); <c>0x</c> and
    /// bare hex are what a person writing a bound by hand tends to produce.</summary>
    private static byte[] ParseBinary(string raw) =>
        Convert.FromHexString(raw.StartsWith("\\x", StringComparison.Ordinal) ? raw[2..]
            : raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
