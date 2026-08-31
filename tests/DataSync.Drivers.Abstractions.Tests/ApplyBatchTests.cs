using DataSync.Drivers.Abstractions;
using Xunit;

namespace DataSync.Drivers.Abstractions.Tests;

/// <summary>
/// The arithmetic every chunked writer shares. Worth its own tests because the ranges are half-open
/// against a 1-based identity — an off-by-one here silently drops a row from an apply, which is the
/// one failure mode chunking must not introduce.
/// </summary>
public sealed class ApplyBatchTests
{
    private static Dictionary<string, string> Options(string? value) =>
        value is null ? [] : new Dictionary<string, string> { [ApplyBatch.OptionName] = value };

    [Fact]
    public void Read_DefaultsWhenUnset_SoChunkingIsWhatAWriterDoesUnlessToldOtherwise() =>
        Assert.Equal(ApplyBatch.DefaultSize, ApplyBatch.Read(Options(null)));

    [Fact]
    public void Read_ZeroMeansTheUnchunkedStatementBack() =>
        Assert.Null(ApplyBatch.Read(Options("0")));

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData("  1000  ", 1000)]
    public void Read_TakesTheConfiguredSize(string raw, int expected) =>
        Assert.Equal(expected, ApplyBatch.Read(Options(raw)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("-5")]
    public void Read_FallsBackRatherThanFailingARunOverATypoInAPerformanceKnob(string raw) =>
        Assert.Equal(ApplyBatch.DefaultSize, ApplyBatch.Read(Options(raw)));

    [Fact]
    public void Ranges_CoverEveryOrdinalExactlyOnce()
    {
        var ranges = ApplyBatch.Ranges(10, 3).ToList();

        Assert.Equal([(0L, 3L), (3L, 6L), (6L, 9L), (9L, 10L)], ranges);
        // The half-open bounds are what make this true: the union of (after, upTo] over the list is
        // 1..10 with nothing repeated and nothing missed.
        var covered = ranges.SelectMany(r => Enumerable.Range((int)r.After + 1, (int)(r.UpTo - r.After))).ToList();
        Assert.Equal(Enumerable.Range(1, 10), covered);
    }

    [Fact]
    public void Ranges_AreOneStatementWhenTheStagedSetFitsInAChunk() =>
        Assert.Equal([(0L, 5L)], ApplyBatch.Ranges(5, 5).ToList());

    [Fact]
    public void Ranges_AreOneStatementWhenChunkingIsOff() =>
        Assert.Equal([(0L, 1_000_000L)], ApplyBatch.Ranges(1_000_000, null).ToList());

    [Fact]
    public void Ranges_AreEmptyForAnEmptyStagedSet_SoNoWriterIssuesAStatementForNothing()
    {
        Assert.Empty(ApplyBatch.Ranges(0, 100));
        Assert.Empty(ApplyBatch.Ranges(0, null));
    }
}
