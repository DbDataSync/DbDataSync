using System.Globalization;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic;

/// <summary>
/// Turns a column's observed MIN/MAX into evenly-sized half-open <see cref="RangeSegment"/> buckets.
/// Kept separate from the reader that calls it so the boundary arithmetic — the part that's easy to get
/// subtly wrong — is pure and unit-testable without a live server.
/// <para>
/// The arithmetic is engine-neutral; only deciding *which* arithmetic a column's type calls for is
/// not, and that comes from <see cref="SqlDialect.ClassifyForBucketing"/>.
/// </para>
/// </summary>
public static class SegmentExpansion
{
    /// <summary>
    /// Builds <paramref name="bucketCount"/> contiguous buckets covering <paramref name="minValue"/>
    /// through <paramref name="maxValue"/> inclusive.
    /// <para>
    /// Boundaries are computed once and consecutive pairs reused, so bucket <c>i</c>'s exclusive upper
    /// bound is bucket <c>i+1</c>'s inclusive lower bound exactly — the buckets tile the range with no
    /// gap and no row covered twice, even where the division doesn't come out even.
    /// </para>
    /// <para>
    /// The final bucket's upper bound is pushed just past <paramref name="maxValue"/> (which a
    /// half-open range would otherwise exclude) by one whole unit rather than by the type's smallest
    /// representable step. Overshooting the top of the range is harmless — there is nothing above MAX
    /// to sweep in — and a "smallest step" that's smaller than the column's own storage resolution
    /// (1 tick against a <c>datetime</c>'s 3.33ms, say) would round back to MAX and silently drop the
    /// maximum row.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RangeSegment> BuildBuckets(
        SqlDialect dialect, string column, string nativeType, object minValue, object maxValue, int bucketCount)
    {
        if (bucketCount < 1)
            throw new InvalidOperationException($"Auto segment on '{column}' needs a bucket count of at least 1.");

        return dialect.ClassifyForBucketing(SqlTypeName.BaseOf(nativeType)) switch
        {
            // Integral columns get integral boundaries: a fractional bound rendered against an INT
            // column can't be bound as one (the parameter is typed to the column), so dividing an
            // integer range into buckets has to floor, not carry a remainder.
            BucketableKind.Integral =>
                IntegralBuckets(column, Convert.ToInt64(minValue, CultureInfo.InvariantCulture),
                    Convert.ToInt64(maxValue, CultureInfo.InvariantCulture), bucketCount),

            BucketableKind.Numeric =>
                NumericBuckets(column, ToDecimal(column, nativeType, minValue), ToDecimal(column, nativeType, maxValue), bucketCount),

            BucketableKind.DateTime =>
                TemporalBuckets(column, (DateTime)minValue, (DateTime)maxValue, bucketCount),

            BucketableKind.DateTimeOffset =>
                OffsetBuckets(column, (DateTimeOffset)minValue, (DateTimeOffset)maxValue, bucketCount),

            _ => throw new InvalidOperationException(
                $"Auto segmentation needs a column it can divide into ranges; '{column}' is " +
                $"'{nativeType}'. Use an explicit list or range segment for this column instead."),
        };
    }

    private static IReadOnlyList<RangeSegment> IntegralBuckets(string column, long min, long max, int bucketCount)
    {
        var boundaries = new long[bucketCount + 1];
        var span = max - min;
        for (var i = 0; i < bucketCount; i++)
            boundaries[i] = min + (span * i / bucketCount);
        boundaries[bucketCount] = max + 1;

        return ToSegments(column, boundaries, b => b.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<RangeSegment> NumericBuckets(string column, decimal min, decimal max, int bucketCount)
    {
        var boundaries = new decimal[bucketCount + 1];
        var span = max - min;
        for (var i = 0; i < bucketCount; i++)
            boundaries[i] = min + (span * i / bucketCount);
        boundaries[bucketCount] = max + 1m;

        return ToSegments(column, boundaries, b => b.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<RangeSegment> TemporalBuckets(string column, DateTime min, DateTime max, int bucketCount)
    {
        var boundaries = new DateTime[bucketCount + 1];
        var span = max.Ticks - min.Ticks;
        for (var i = 0; i < bucketCount; i++)
            boundaries[i] = new DateTime(min.Ticks + (span * i / bucketCount), min.Kind);
        boundaries[bucketCount] = max.AddDays(1);

        return ToSegments(column, boundaries, b => b.ToString("O", CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<RangeSegment> OffsetBuckets(string column, DateTimeOffset min, DateTimeOffset max, int bucketCount)
    {
        var boundaries = new DateTimeOffset[bucketCount + 1];
        var span = max.UtcTicks - min.UtcTicks;
        for (var i = 0; i < bucketCount; i++)
            boundaries[i] = min.AddTicks(span * i / bucketCount);
        boundaries[bucketCount] = max.AddDays(1);

        return ToSegments(column, boundaries, b => b.ToString("O", CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<RangeSegment> ToSegments<T>(string column, T[] boundaries, Func<T, string> format)
    {
        var segments = new List<RangeSegment>(boundaries.Length - 1);
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            // A degenerate range (min == max, or more buckets than distinct values) would produce
            // empty buckets whose bounds are equal. They're dropped rather than enqueued: an empty
            // half-open range matches nothing, so its only effect for a reconciling writer would be a
            // no-op run, and for a bucket count larger than the range it would be many of them.
            if (EqualityComparer<T>.Default.Equals(boundaries[i], boundaries[i + 1]))
                continue;
            segments.Add(new RangeSegment(column, format(boundaries[i]), format(boundaries[i + 1])));
        }
        return segments;
    }

    private static decimal ToDecimal(string column, string nativeType, object value)
    {
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Column '{column}' ({nativeType}) holds values outside the range auto segmentation can " +
                "divide up. Use explicit range segments for this column instead.", ex);
        }
    }
}
