using DbDataSync.Core.Config;
using DbDataSync.Scripting;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// The DuckDB authoring path, which is the one that needs no connection to anything — so its whole
/// behaviour is testable without a server, which is most of why it is the default.
/// </summary>
public sealed class SegmentingStrategyRunnerTests
{
    private static readonly SegmentingStrategyRunner Runner = new(null!);

    private static SegmentingStrategyConfig DuckDb(string sql, string column = "OrderDate") => new()
    {
        Name = "year-month",
        Kind = SegmentingStrategyKind.DuckDb,
        Column = column,
        Sql = sql,
    };

    private static Task<IReadOnlyList<Abstractions.SegmentCandidate>> RunAsync(SegmentingStrategyConfig strategy) =>
        Runner.RunAsync(strategy, null!, null, null, CancellationToken.None);

    /// <summary>The worked example from the plan doc, run for real.</summary>
    [Fact]
    public async Task GenerateSeries_ProducesOneLabelledMonthPerRow()
    {
        var candidates = await RunAsync(DuckDb("""
            SELECT
                strftime(d, '%Y-%m')   AS label,
                d                      AS range_start,
                d + INTERVAL 1 MONTH   AS range_end
            FROM generate_series(DATE '2024-01-01', DATE '2024-04-01', INTERVAL 1 MONTH) AS t(d)
            """));

        Assert.Equal(4, candidates.Count);
        Assert.Equal(["2024-01", "2024-02", "2024-03", "2024-04"], candidates.Select(c => c.Label));

        var march = candidates[2];
        var segment = Assert.IsType<RangeSegment>(march.Segment);
        Assert.Equal("OrderDate", segment.Column);
        // Half-open, and each month's end is the next month's start — the same tiling an Auto
        // expansion produces, which is what makes consecutive segments cover the space exactly once.
        Assert.StartsWith("2024-03-01", segment.RangeMin);
        Assert.StartsWith("2024-04-01", segment.RangeMax);
        Assert.Equal(segment.RangeMax, ((RangeSegment)candidates[3].Segment).RangeMin);
    }

    [Fact]
    public async Task TheLabelBecomesTheSegmentsDescription()
    {
        var candidates = await RunAsync(DuckDb(
            "SELECT '2024-03' AS label, DATE '2024-03-01' AS range_start, DATE '2024-04-01' AS range_end"));

        // Not the generated "OrderDate [.., ..)" — a strategy naming its segments is the whole point.
        Assert.Equal("2024-03", candidates[0].Segment.Describe());
    }

    [Fact]
    public async Task Selected_IsCarriedThroughPerRow()
    {
        var candidates = await RunAsync(DuckDb("""
            SELECT * FROM (VALUES
                ('a', DATE '2024-01-01', DATE '2024-02-01', true),
                ('b', DATE '2024-02-01', DATE '2024-03-01', false)
            ) AS t(label, range_start, range_end, selected)
            """));

        Assert.True(candidates[0].Selected);
        Assert.False(candidates[1].Selected);
    }

    /// <summary>
    /// A strategy that says nothing about selection is proposing, not deciding — so unattended it does
    /// nothing rather than reloading every segment it can imagine.
    /// </summary>
    [Fact]
    public async Task AnAbsentSelectedColumn_MeansNothingIsPreSelected()
    {
        var candidates = await RunAsync(DuckDb(
            "SELECT 'a' AS label, DATE '2024-01-01' AS range_start, DATE '2024-02-01' AS range_end"));

        Assert.All(candidates, c => Assert.False(c.Selected));
    }

    [Fact]
    public async Task AMissingRequiredColumn_IsNamedRatherThanFailingOnAnOrdinal()
    {
        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(DuckDb(
            "SELECT 'a' AS label, DATE '2024-01-01' AS range_start")));

        Assert.Contains("range_end", problem.Message);
        Assert.Contains("It returned: label, range_start", problem.Message);
    }

    [Fact]
    public async Task AStrategyWithNoColumn_SaysWhatItIsMissing()
    {
        var strategy = DuckDb("SELECT 1", column: "");

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(strategy));

        Assert.Contains("which column its ranges are over", problem.Message);
    }

    [Fact]
    public async Task ASourceSqlStrategyWithNoConnection_RefusesPlainly()
    {
        var strategy = new SegmentingStrategyConfig
        {
            Name = "s",
            Kind = SegmentingStrategyKind.SourceSql,
            Column = "Id",
            Sql = "SELECT 1",
        };

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(strategy));

        Assert.Contains("no source connection is available", problem.Message);
    }

    /// <summary>
    /// Bounds are re-bound against the source's own column type later, so they have to be written in a
    /// form that parses back the same way whatever the server's locale is.
    /// </summary>
    [Fact]
    public async Task DateBounds_AreWrittenRoundTrippable()
    {
        var candidates = await RunAsync(DuckDb(
            "SELECT 'a' AS label, DATE '2024-03-01' AS range_start, DATE '2024-04-01' AS range_end"));

        var segment = (RangeSegment)candidates[0].Segment;
        Assert.Equal(new DateTime(2024, 3, 1), DateTime.Parse(segment.RangeMin, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OnlyDuckDbNeedsNoConnection_AndItGetsNone()
    {
        // The claim the whole default rests on: previewing a DuckDB strategy is safe because there is
        // nothing for it to touch. Passing nulls for both connections proves it never asks.
        var candidates = await RunAsync(DuckDb("SELECT 'a' AS label, 1 AS range_start, 2 AS range_end", "Id"));

        Assert.Single(candidates);
    }
}
