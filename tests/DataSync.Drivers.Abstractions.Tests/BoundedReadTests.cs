using DataSync.Drivers.Abstractions;
using Xunit;

namespace DataSync.Drivers.Abstractions.Tests;

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
}
