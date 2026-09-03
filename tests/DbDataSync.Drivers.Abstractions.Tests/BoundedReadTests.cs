using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Abstractions.Tests;

/// <summary>
/// How a reader reads its row cap out of an options bag. Worth its own tests because the same option
/// key now means two different things depending on who asks: unbounded-until-configured for a scan
/// over a column the operator chose, capped-by-default for a log-based reader whose ordering column is
/// the change table's clustered key (phase 84).
/// </summary>
public sealed class BoundedReadTests
{
    private static Dictionary<string, string> Options(string? value) =>
        value is null ? [] : new Dictionary<string, string> { [BoundedRead.OptionName] = value };

    [Fact]
    public void Unset_LeavesAWatermarkScanUnbounded() =>
        Assert.Null(BoundedRead.Read(Options(null)));

    [Fact]
    public void Unset_CapsALogBasedReaderAtTheDefault() =>
        Assert.Equal(
            BoundedRead.DefaultMaxRows, BoundedRead.Read(Options(null), BoundedRead.DefaultMaxRows));

    /// <summary>
    /// The escape hatch, and the reason "unset" and "zero" cannot be the same answer. Defaulting the
    /// cap on is only defensible if an operator can still ask for the whole window back, and after this
    /// phase the empty option no longer means that for CDC or Change Tracking.
    /// </summary>
    [Fact]
    public void Zero_IsHowAnOperatorAsksForTheWholeWindowBack() =>
        Assert.Null(BoundedRead.Read(Options("0"), BoundedRead.DefaultMaxRows));

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData("  1000  ", 1000)]
    public void AConfiguredCapWins(string raw, int expected)
    {
        Assert.Equal(expected, BoundedRead.Read(Options(raw)));
        Assert.Equal(expected, BoundedRead.Read(Options(raw), BoundedRead.DefaultMaxRows));
    }

    /// <summary>
    /// A typo falls back rather than failing the run — and, for a capped-by-default reader, falls back
    /// to the cap rather than to unbounded. Reading "not a number" as "read everything" would turn a
    /// mistyped knob into precisely the uncapped pass this phase exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("-5")]
    public void ATypoFallsBackToWhicheverDefaultTheReaderNamed(string raw)
    {
        Assert.Null(BoundedRead.Read(Options(raw)));
        Assert.Equal(BoundedRead.DefaultMaxRows, BoundedRead.Read(Options(raw), BoundedRead.DefaultMaxRows));
    }

    /// <summary>
    /// One option key, two descriptors, so a reader that caps by default does not tell an operator
    /// that leaving the field empty reads the whole window — which was true before this phase and is
    /// now true only for the watermark scan.
    /// </summary>
    [Fact]
    public void BothDescriptorsDescribeTheSameOption_AndOnlyOneAdvertisesADefault()
    {
        Assert.Equal(BoundedRead.OptionName, BoundedRead.Descriptor.Name);
        Assert.Equal(BoundedRead.OptionName, BoundedRead.CappedDescriptor.Name);

        Assert.Null(BoundedRead.Descriptor.Default);
        Assert.Equal(BoundedRead.DefaultMaxRows.ToString(), BoundedRead.CappedDescriptor.Default);
    }

    // ---- The commit time that travels with the position (phase 87) ------------------------------
    //
    // ReadResult carries two positions — the one computed up front and the one a capped pass actually
    // reached — and now a time for each. The pairing rule is that a caller gets the time belonging to
    // the position it is going to store, or no time; never the other one's.

    private static ReadResult Result(
        string newWatermark, DateTimeOffset? newWatermarkTime, BoundedReadPosition? bounded) =>
        new(Rows(), newWatermark, Diagnostics: null, bounded, newWatermarkTime);

    private static async IAsyncEnumerable<ChangeRow> Rows() { await Task.CompletedTask; yield break; }

    private static readonly DateTimeOffset WindowEnd = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastRow = new(2026, 3, 1, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnboundedRead_ReportsTheTimeOfTheWatermarkItComputedUpFront()
    {
        var result = Result("100", WindowEnd, bounded: null);

        Assert.Equal("100", result.WatermarkAfterRead);
        Assert.Equal(WindowEnd, result.WatermarkTimeAfterRead);
    }

    /// <summary>
    /// A capped pass that drained its whole window advances to the window's end, so the time it
    /// reports is the window end's — the same coalesce the position itself makes.
    /// </summary>
    [Fact]
    public void ABoundedReadThatDrainedItsWindow_ReportsTheWindowEndsTime()
    {
        var result = Result("100", WindowEnd, new BoundedReadPosition());

        Assert.Equal("100", result.WatermarkAfterRead);
        Assert.Equal(WindowEnd, result.WatermarkTimeAfterRead);
    }

    [Fact]
    public void ABoundedReadTheCapCutShort_ReportsTheTimeOfThePositionItReached()
    {
        var result = Result(
            "100", WindowEnd, new BoundedReadPosition { Reached = "60", ReachedTimeUtc = LastRow });

        Assert.Equal("60", result.WatermarkAfterRead);
        Assert.Equal(LastRow, result.WatermarkTimeAfterRead);
    }

    /// <summary>
    /// **The case a plain null-coalesce would get wrong, and the reason the property keys off
    /// <c>Reached</c> instead.** A pass cut short at version 60 whose commit time could not be placed
    /// must report no time — coalescing would hand back the *window end's* time, a figure describing a
    /// position this pass is not storing. On a mapping draining a backlog under a row cap, that is
    /// exactly the mapping furthest behind reporting itself as caught up.
    /// </summary>
    [Fact]
    public void ABoundedReadWhosePositionWouldNotMap_ReportsNoTimeRatherThanTheWindowEnds()
    {
        var result = Result("100", WindowEnd, new BoundedReadPosition { Reached = "60" });

        Assert.Equal("60", result.WatermarkAfterRead);
        Assert.Null(result.WatermarkTimeAfterRead);
    }

    /// <summary>Every other reader leaves the field alone, and gets a null rather than a
    /// default-valued instant that would read as 1 January year 1.</summary>
    [Fact]
    public void AReaderThatCannotPlaceItsPositions_ReportsNoTimeAtAll()
    {
        var result = Result("100", newWatermarkTime: null, bounded: null);

        Assert.Equal("100", result.WatermarkAfterRead);
        Assert.Null(result.WatermarkTimeAfterRead);
    }
}
