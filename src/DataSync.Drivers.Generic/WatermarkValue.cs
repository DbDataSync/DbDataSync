using System.Globalization;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Formats a watermark for storage, and it has to round-trip: the value goes into the work queue as
/// text and comes back to be bound against the same column on the next pass.
/// <para>
/// <see cref="Convert.ToString(object?)"/> is not good enough for a timestamp — its general format
/// carries no fractional seconds, so a watermark of <c>09:00:00.500</c> is stored as
/// <c>09:00:00</c> and every row in that half-second is read again next pass. Harmless (writers
/// upsert) but wasteful, and invisible until someone counts the rows.
/// </para>
/// </summary>
public static class WatermarkValue
{
    public static string Format(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        // A SQL Server rowversion/timestamp column, which is the other common watermark shape.
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
