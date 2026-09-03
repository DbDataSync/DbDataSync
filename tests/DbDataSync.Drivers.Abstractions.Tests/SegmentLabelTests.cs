using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Abstractions.Tests;

/// <summary>
/// A segment's description becomes the run's SegmentLabel in run history, so it is what an operator
/// actually reads. The label is additive — these pin both that a strategy's own name wins and that
/// nothing built the old way changed.
/// </summary>
public sealed class SegmentLabelTests
{
    [Fact]
    public void Range_WithoutALabel_DescribesItselfByItsBounds() =>
        Assert.Equal(
            "OrderDate [2024-03-01, 2024-04-01)",
            new RangeSegment("OrderDate", "2024-03-01", "2024-04-01").Describe());

    [Fact]
    public void Range_WithALabel_UsesIt() =>
        Assert.Equal("2024-03", new RangeSegment("OrderDate", "2024-03-01", "2024-04-01", "2024-03").Describe());

    /// <summary>
    /// A label is part of what a segment *is*, not decoration on it: two ranges over the same bounds
    /// with different names are two different proposals, and a dedupe that merged them would drop one
    /// of an operator's checked boxes.
    /// </summary>
    [Fact]
    public void Range_LabelParticipatesInEquality()
    {
        var bounds = new RangeSegment("OrderDate", "2024-03-01", "2024-04-01");

        Assert.NotEqual(bounds, new RangeSegment("OrderDate", "2024-03-01", "2024-04-01", "March"));
        Assert.Equal(bounds, new RangeSegment("OrderDate", "2024-03-01", "2024-04-01"));
    }
}
