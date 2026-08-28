using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Characteristic values for a column's canonical type — the ones a transform is most likely to get
/// wrong.
/// <para>
/// **Not random.** A random value makes a test that passes today and fails next Tuesday for reasons
/// nobody can reproduce, which is worse than no test. These are fixed and chosen: an empty string as
/// well as an ordinary one, a zero and a negative, a null wherever the column allows one, a date that
/// is not today. What they are testing for is the null check that was never written and the
/// <c>Substring</c> that assumes a non-empty string.
/// </para>
/// </summary>
public static class SampleValues
{
    /// <summary>A fixed instant, not <c>UtcNow</c>: a generated sample that changes every time it is
    /// asked cannot be compared against the last answer, which is the first thing anyone does with
    /// one.</summary>
    public static readonly DateTime Instant = new(2024, 3, 17, 9, 41, 22, DateTimeKind.Utc);

    /// <summary>
    /// In order: the ordinary case first, because it is the one an operator reads to check the
    /// transform does what they meant. The awkward ones follow, because they are the ones that make
    /// it throw.
    /// </summary>
    public static IReadOnlyList<object?> For(CanonicalType type, bool isNullable)
    {
        var values = new List<object?>(Characteristic(type));
        if (isNullable)
            values.Add(null);
        return values;
    }

    private static IEnumerable<object?> Characteristic(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => [true, false],
        CanonicalTypeKind.Int8 => [(byte)7, (byte)0],
        CanonicalTypeKind.Int16 => [(short)42, (short)0, (short)-1],
        CanonicalTypeKind.Int32 => [42, 0, -1],
        CanonicalTypeKind.Int64 => [42L, 0L, -1L],
        CanonicalTypeKind.Decimal => [12.34m, 0m, -0.01m],
        CanonicalTypeKind.Float => [1.5f, 0f],
        CanonicalTypeKind.Double => [1.5d, 0d],
        CanonicalTypeKind.String => Strings(type),
        CanonicalTypeKind.Binary => [new byte[] { 1, 2, 3 }, Array.Empty<byte>()],
        CanonicalTypeKind.Date => [DateOnly.FromDateTime(Instant)],
        CanonicalTypeKind.Time => [TimeOnly.FromDateTime(Instant)],
        CanonicalTypeKind.Timestamp => [Instant],
        CanonicalTypeKind.TimestampTz => [new DateTimeOffset(Instant)],
        CanonicalTypeKind.Guid => [new Guid("00000000-0000-4000-8000-000000000042")],
        CanonicalTypeKind.Json => ["""{"sample":true}"""],
        CanonicalTypeKind.Xml => ["<sample/>"],
        // Nothing is invented for a type no engine could translate. A generated value for one would be
        // a guess about a type the whole canonical system declines to guess about.
        _ => [null],
    };

    /// <summary>
    /// An ordinary value, an empty one, and — where the column is long enough to hold it — one at the
    /// declared length, which is what finds a transform that appends without checking.
    /// </summary>
    private static IEnumerable<object?> Strings(CanonicalType type)
    {
        yield return "sample";
        yield return "";

        var length = type.IsMax ? 64 : type.Length ?? 0;
        if (length > "sample".Length)
            yield return new string('x', Math.Min(length, 64));
    }
}
