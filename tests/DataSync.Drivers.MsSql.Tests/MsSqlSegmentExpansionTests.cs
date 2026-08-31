using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// Bucket-boundary arithmetic. The properties that matter are that buckets tile the observed range
/// with no gap and no overlap, and that MAX itself lands inside a bucket despite the ranges being
/// half-open — both are easy to get subtly wrong and invisible until rows go missing from a reload.
/// </summary>
public sealed class MsSqlSegmentExpansionTests
{
    private static List<(string Min, string Max)> Bounds(IReadOnlyList<RangeSegment> segments) =>
        segments.Select(s => (s.RangeMin, s.RangeMax)).ToList();

    [Fact]
    public void IntegerRange_DividesEvenlyAndCoversMax()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "int", 1, 10, 3);

        Assert.Equal([("1", "4"), ("4", "7"), ("7", "11")], Bounds(segments));
    }

    /// <summary>Integral columns get integral boundaries: a fractional bound can't be bound to an INT
    /// parameter, so an uneven division has to floor rather than carry a remainder.</summary>
    [Fact]
    public void IntegerRange_ThatDoesNotDivideEvenly_StillProducesIntegerBounds()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "int", 1, 10, 4);

        Assert.Equal([("1", "3"), ("3", "5"), ("5", "7"), ("7", "11")], Bounds(segments));
        Assert.All(segments, s => Assert.DoesNotContain(".", s.RangeMin));
    }

    [Fact]
    public void Buckets_TileWithoutGapOrOverlap()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "bigint", 100L, 1000L, 7);

        for (var i = 0; i < segments.Count - 1; i++)
            Assert.Equal(segments[i].RangeMax, segments[i + 1].RangeMin);
    }

    [Fact]
    public void DecimalRange_KeepsFractionalBoundaries()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "Amount", "decimal(18,2)", 0m, 10m, 4);

        Assert.Equal([("0", "2.5"), ("2.5", "5"), ("5", "7.5"), ("7.5", "11")], Bounds(segments));
    }

    [Fact]
    public void DateRange_DividesOnTicksAndPushesTheTopBoundPastMax()
    {
        var min = new DateTime(2024, 1, 1);
        var max = new DateTime(2024, 1, 11);

        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderDate", "datetime2(7)", min, max, 2);

        Assert.Equal(2, segments.Count);
        Assert.Equal(min.ToString("O"), segments[0].RangeMin);
        Assert.Equal(new DateTime(2024, 1, 6).ToString("O"), segments[0].RangeMax);
        Assert.Equal(max.AddDays(1).ToString("O"), segments[1].RangeMax);
    }

    /// <summary>The last bucket's upper bound overshoots by a whole unit rather than by the type's
    /// smallest step: a step below the column's own storage resolution rounds back to MAX and drops
    /// the maximum row, and overshooting the top of the observed range costs nothing.</summary>
    [Fact]
    public void TopBound_IsStrictlyAboveMaxForCoarseResolutionDateTimes()
    {
        var max = new DateTime(2024, 6, 30, 12, 0, 0);

        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderDate", "datetime", new DateTime(2024, 1, 1), max, 3);

        Assert.True(DateTime.Parse(segments[^1].RangeMax) > max);
    }

    [Fact]
    public void SingleBucket_CoversTheWholeRange()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "int", 5, 42, 1);

        Assert.Equal([("5", "43")], Bounds(segments));
    }

    /// <summary>More buckets than distinct values collapses to the buckets that actually hold rows
    /// rather than enqueueing a pile of empty no-op runs.</summary>
    [Fact]
    public void MoreBucketsThanValues_DropsTheEmptyOnes()
    {
        var segments = SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "int", 7, 7, 5);

        Assert.Equal([("7", "8")], Bounds(segments));
    }

    [Fact]
    public void ZeroBuckets_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "OrderId", "int", 1, 10, 0));

        Assert.Contains("at least 1", ex.Message);
    }

    [Fact]
    public void UndividableColumnType_IsRejectedWithAnActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SegmentExpansion.BuildBuckets(MsSqlDialect.Instance, "Id", "uniqueidentifier", Guid.NewGuid(), Guid.NewGuid(), 4));

        Assert.Contains("list or range segment", ex.Message);
    }
}
