using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// A check against a historized target reports the feature working as a defect: an SCD Type 2 or
/// Snapshot target holds more rows than its source by design, so every run says the counts differ.
/// This is the filter that compares like with like.
/// </summary>
public sealed class CurrentOnlyComparisonTests
{
    private static readonly VerificationStatement.VerificationColumn[] Region =
        [new("[Region]", "Region")];

    /// <summary>An SCD Type 2 target has a per-row flag: one row per key is current.</summary>
    [Fact]
    public void AnScd2Target_IsFilteredByItsCurrentFlag()
    {
        var predicate = VerificationStatement.CurrentOnlyPredicate(
            BracketDialect.Instance, "dbo", "Hist", HistorizedColumns.IsCurrent);

        Assert.Equal("[DS_IsCurrent] = TRUE", predicate);
    }

    /// <summary>
    /// A Snapshot target has no flag — every snapshot is a complete separate copy, so "current" means
    /// the most recent one. A different filter, which is why this is not one shape assumed universally.
    /// </summary>
    [Fact]
    public void ASnapshotTarget_IsFilteredToTheMostRecentSnapshot()
    {
        var predicate = VerificationStatement.CurrentOnlyPredicate(
            BracketDialect.Instance, "dbo", "Hist", HistorizedColumns.SnapshotAt);

        Assert.Equal("[DS_SnapshotAt] = (SELECT MAX([DS_SnapshotAt]) FROM [dbo].[Hist])", predicate);
    }

    [Fact]
    public void WithNoColumn_ThereIsNoPredicate() =>
        Assert.Null(VerificationStatement.CurrentOnlyPredicate(BracketDialect.Instance, "dbo", "Hist", null));

    /// <summary>Additive, not a silent behaviour change: a check that does not ask for it builds
    /// exactly the statement it built before.</summary>
    [Fact]
    public void WithoutTheOption_TheStatementIsUnchanged()
    {
        var sql = VerificationStatement.BuildRowCount(
            BracketDialect.Instance, "dbo", "Hist", Region, filter: null);

        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public void WithTheOption_TheTargetIsNarrowed()
    {
        var sql = VerificationStatement.BuildRowCount(
            BracketDialect.Instance, "dbo", "Hist", Region, filter: null,
            currentOnly: "[DS_IsCurrent] = TRUE");

        Assert.Contains("WHERE [DS_IsCurrent] = TRUE", sql);
    }

    /// <summary>
    /// Both, and the check's own filter is parenthesised. An operator's filter is an arbitrary
    /// expression, and one containing an OR would otherwise swallow the AND and silently compare
    /// every version again.
    /// </summary>
    [Fact]
    public void AChecksOwnFilter_IsParenthesisedBeforeBeingAndedWithIt()
    {
        var sql = VerificationStatement.BuildRowCount(
            BracketDialect.Instance, "dbo", "Hist", Region,
            filter: "Region = 'north' OR Region = 'south'",
            currentOnly: "[DS_IsCurrent] = TRUE");

        Assert.Contains("WHERE (Region = 'north' OR Region = 'south') AND [DS_IsCurrent] = TRUE", sql);
    }

    [Fact]
    public void ASumCheck_IsNarrowedTheSameWay()
    {
        var sql = VerificationStatement.BuildSum(
            BracketDialect.Instance, "dbo", "Hist", Region, [new("[Amount]", "Amount")],
            filter: null, currentOnly: "[DS_IsCurrent] = TRUE");

        Assert.Contains("SUM([Amount])", sql);
        Assert.Contains("WHERE [DS_IsCurrent] = TRUE", sql);
    }

    /// <summary>The boolean literal comes from the dialect, for the same reason the SCD2 writer's
    /// does — `= 1` against a Postgres boolean is "operator does not exist".</summary>
    [Fact]
    public void TheFlagComparison_ComesFromTheDialect()
    {
        var predicate = VerificationStatement.CurrentOnlyPredicate(
            ColonDialect.Instance, "public", "hist", HistorizedColumns.IsCurrent);

        Assert.Equal("\"DS_IsCurrent\" = TRUE", predicate);
    }
}
