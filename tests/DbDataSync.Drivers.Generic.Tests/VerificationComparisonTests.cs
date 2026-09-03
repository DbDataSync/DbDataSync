using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Lining up two result sets and saying where they disagree. The threshold cases matter most: the
/// premise of the whole feature is that a replication is behind by design, so a comparison that calls
/// every mid-pass difference a failure teaches an operator to ignore it.
/// </summary>
public sealed class VerificationComparisonTests
{
    private static VerificationSide Side(params (string Group, double Rows)[] rows) =>
        new(
            [.. rows.Select(r => (IReadOnlyList<string>)[r.Group])],
            [.. rows.Select(r => (IReadOnlyDictionary<string, double>)new Dictionary<string, double> { ["__rows"] = r.Rows })],
            DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<VerificationRow> Compare(
        VerificationSide source, VerificationSide target, double threshold = 0) =>
        VerificationComparison.Compare(source, target, threshold);

    [Fact]
    public void TwoIdenticalSides_AreAllMatches()
    {
        var rows = Compare(Side(("north", 10), ("south", 20)), Side(("north", 10), ("south", 20)));

        Assert.All(rows, r => Assert.Equal(VerificationRowStatus.Match, r.Status));
        Assert.All(rows, r => Assert.Equal(0, r.Differences["__rows"]));
    }

    [Fact]
    public void ADifference_IsReportedAsTargetMinusSource()
    {
        var row = Assert.Single(Compare(Side(("north", 10)), Side(("north", 7))));

        Assert.Equal(VerificationRowStatus.Differs, row.Status);
        Assert.Equal(-3, row.Differences["__rows"]);
    }

    /// <summary>A group on one side only is named, not silently dropped — which is the difference
    /// between a signal and a diagnosis.</summary>
    [Fact]
    public void AGroupOnOneSideOnly_IsReportedAsMissingFromTheOther()
    {
        var rows = Compare(Side(("north", 10), ("west", 5)), Side(("east", 3), ("north", 10)));

        Assert.Equal(
            [VerificationRowStatus.MissingFromSource, VerificationRowStatus.Match, VerificationRowStatus.MissingFromTarget],
            rows.Select(r => r.Status));
        Assert.Equal(["east"], rows[0].Group);
        Assert.Equal(["west"], rows[2].Group);
    }

    [Fact]
    public void EitherSideRunningOut_LeavesTheRestReportedRatherThanTruncated()
    {
        Assert.Equal(3, Compare(Side(("a", 1), ("b", 1), ("c", 1)), Side(("a", 1))).Count);
        Assert.Equal(3, Compare(Side(("a", 1)), Side(("a", 1), ("b", 1), ("c", 1))).Count);
    }

    // ---- The threshold ----

    /// <summary>
    /// A difference under the threshold is still reported as a number — an operator reads it to judge
    /// drift, and hiding the small ones would leave them unable to tell "equal" from "close" — but it
    /// is not called a failure.
    /// </summary>
    [Fact]
    public void ADifferenceUnderTheThreshold_IsAMatchThatStillCarriesItsNumber()
    {
        var row = Assert.Single(Compare(Side(("north", 1000)), Side(("north", 995)), threshold: 0.01));

        Assert.Equal(VerificationRowStatus.Match, row.Status);
        Assert.Equal(-5, row.Differences["__rows"]);
    }

    [Fact]
    public void ADifferenceOverTheThreshold_IsAFailure()
    {
        var row = Assert.Single(Compare(Side(("north", 1000)), Side(("north", 900)), threshold: 0.01));

        Assert.Equal(VerificationRowStatus.Differs, row.Status);
    }

    /// <summary>
    /// Relative to the larger side, so the same threshold means the same thing for a group of ten rows
    /// and a group of ten million — which is what makes one number usable across a whole mapping.
    /// </summary>
    [Fact]
    public void TheThresholdIsProportional_NotAbsolute()
    {
        // Both are one part in a hundred, so a 2% threshold clears both and a 0.5% clears neither.
        Assert.All(
            Compare(Side(("a", 100), ("b", 1_000_000)), Side(("a", 99), ("b", 990_000)), threshold: 0.02),
            r => Assert.Equal(VerificationRowStatus.Match, r.Status));

        Assert.All(
            Compare(Side(("a", 100), ("b", 1_000_000)), Side(("a", 99), ("b", 990_000)), threshold: 0.005),
            r => Assert.Equal(VerificationRowStatus.Differs, r.Status));
    }

    [Fact]
    public void AZeroThreshold_MeansAnyDifferenceAtAll()
    {
        var row = Assert.Single(Compare(Side(("north", 1_000_000)), Side(("north", 999_999))));

        Assert.Equal(VerificationRowStatus.Differs, row.Status);
    }

    /// <summary>
    /// There is no proportion of nothing: a measure that went from zero to anything is a change of
    /// unbounded relative size, and treating it as within any threshold would hide the appearance of
    /// rows in a group that had none.
    /// </summary>
    [Fact]
    public void AMeasureThatWasZero_IsAlwaysOverTheThreshold()
    {
        var row = Assert.Single(Compare(Side(("north", 0)), Side(("north", 1)), threshold: 0.99));

        Assert.Equal(VerificationRowStatus.Differs, row.Status);
    }

    [Fact]
    public void TwoZeroes_AreEqualRatherThanADivisionByZero()
    {
        var row = Assert.Single(Compare(Side(("north", 0)), Side(("north", 0)), threshold: 0.5));

        Assert.Equal(VerificationRowStatus.Match, row.Status);
    }

    // ---- Shape ----

    [Fact]
    public void AnUngroupedCheck_IsOneRowWithNoGroup()
    {
        var source = new VerificationSide(
            [[]], [new Dictionary<string, double> { ["__rows"] = 42 }], DateTimeOffset.UnixEpoch);
        var target = new VerificationSide(
            [[]], [new Dictionary<string, double> { ["__rows"] = 42 }], DateTimeOffset.UnixEpoch);

        var row = Assert.Single(VerificationComparison.Compare(source, target, 0));

        Assert.Empty(row.Group);
        Assert.Equal(VerificationRowStatus.Match, row.Status);
    }

    [Fact]
    public void MultipleMeasures_AreComparedIndependently()
    {
        var source = new VerificationSide(
            [["north"]],
            [new Dictionary<string, double> { ["Amount"] = 100, ["Quantity"] = 5 }],
            DateTimeOffset.UnixEpoch);
        var target = new VerificationSide(
            [["north"]],
            [new Dictionary<string, double> { ["Amount"] = 100, ["Quantity"] = 4 }],
            DateTimeOffset.UnixEpoch);

        var row = Assert.Single(VerificationComparison.Compare(source, target, 0));

        Assert.Equal(0, row.Differences["Amount"]);
        Assert.Equal(-1, row.Differences["Quantity"]);
        Assert.Equal(VerificationRowStatus.Differs, row.Status);
    }

    /// <summary>Grouping values are compared as the text each engine rendered, and by more than one
    /// column when a check names more than one — where a naive comparison stops at the first.</summary>
    [Fact]
    public void MultiColumnGroups_AreOrderedByEveryColumn()
    {
        var source = new VerificationSide(
            [["north", "a"], ["north", "b"]],
            [Rows(1), Rows(2)], DateTimeOffset.UnixEpoch);
        var target = new VerificationSide(
            [["north", "b"]], [Rows(2)], DateTimeOffset.UnixEpoch);

        var rows = VerificationComparison.Compare(source, target, 0);

        Assert.Equal(
            [VerificationRowStatus.MissingFromTarget, VerificationRowStatus.Match],
            rows.Select(r => r.Status));

        static IReadOnlyDictionary<string, double> Rows(double n) => new Dictionary<string, double> { ["__rows"] = n };
    }
}
