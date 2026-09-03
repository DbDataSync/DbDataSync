using DbDataSync.Core.Config;
using DbDataSync.Drivers.DuckDb;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>
/// What a segment does to an operator's query text. Asserted against the text rather than against
/// rows, because the text is the contract: the operator wrote the <c>WHERE</c> clause and needs to
/// know exactly what lands where they put a token.
/// </summary>
public sealed class QuerySegmentTokenTests
{
    private const string Ranged =
        "SELECT * FROM orders WHERE {{segmentColumn}} >= {{segmentMin}} AND {{segmentColumn}} < {{segmentMax}}";

    [Fact]
    public void RangeSegment_SubstitutesColumnAndBothBounds()
    {
        var query = QuerySegmentTokens.Substitute(
            Ranged, new RangeSegment("OrderDate", "2024-01-01", "2024-02-01"));

        Assert.Equal(
            "SELECT * FROM orders WHERE \"OrderDate\" >= '2024-01-01' AND \"OrderDate\" < '2024-02-01'",
            query);
    }

    [Fact]
    public void RangeSegment_SubstitutesEveryOccurrenceOfTheColumn()
    {
        // Two in the query above, and the operator may well write more. Replace, not ReplaceFirst.
        var query = QuerySegmentTokens.Substitute(Ranged, new RangeSegment("Id", "1", "100"));

        Assert.Equal(2, query.Split("\"Id\"").Length - 1);
    }

    [Fact]
    public void ListSegment_SubstitutesValuesAsACommaSeparatedLiteralList()
    {
        var query = QuerySegmentTokens.Substitute(
            "SELECT * FROM orders WHERE {{segmentColumn}} IN ({{segmentValues}})",
            new ListSegment("Region", ["EMEA", "APAC"]));

        Assert.Equal("SELECT * FROM orders WHERE \"Region\" IN ('EMEA', 'APAC')", query);
    }

    /// <summary>
    /// A value with a quote in it arrives from config by accident far more often than by attack, and
    /// the failure is identical either way: the statement stops parsing where the operator's own SQL
    /// was supposed to continue.
    /// </summary>
    [Fact]
    public void Literals_HaveTheirQuotesDoubled()
    {
        var query = QuerySegmentTokens.Substitute(
            "WHERE {{segmentColumn}} IN ({{segmentValues}})",
            new ListSegment("Name", ["O'Brien"]));

        Assert.Equal("WHERE \"Name\" IN ('O''Brien')", query);
    }

    [Fact]
    public void Identifiers_HaveTheirQuotesDoubled()
    {
        var query = QuerySegmentTokens.Substitute(
            "WHERE {{segmentColumn}} >= {{segmentMin}} AND {{segmentColumn}} < {{segmentMax}}",
            new RangeSegment("we\"ird", "a", "b"));

        Assert.StartsWith("WHERE \"we\"\"ird\" >= 'a'", query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("full")]
    public void Unsegmented_LeavesTheQueryExactlyAsWritten(string? mode)
    {
        BatchReloadSegment? segment = mode is null ? null : new FullSegment();

        Assert.Equal(Ranged, QuerySegmentTokens.Substitute(Ranged, segment));
    }

    /// <summary>A query with no tokens is not a query to be helped — it runs as written, segmented or
    /// not, and every segment reads the same rows. That is the operator's choice to make.</summary>
    [Fact]
    public void AQueryWithNoTokens_IsUntouchedByASegment()
    {
        const string plain = "SELECT 1";

        Assert.Equal(plain, QuerySegmentTokens.Substitute(plain, new RangeSegment("Id", "1", "9")));
    }

    /// <summary>
    /// Neither marker ever reaches a runtime reader — they are expanded before dispatch. Asserted
    /// anyway because the failure if one ever did is a silent one: substituting nothing would run the
    /// unsegmented query under a segment's name and write a different set of rows than the run history
    /// says it did.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("custom")]
    public void AMarkerSegment_ThrowsRatherThanSubstitutingNothing(string kind)
    {
        BatchReloadSegment segment = kind == "auto"
            ? new AutoSegment("Id", 4)
            : new CustomSegment("last-three-months");

        var ex = Assert.Throws<InvalidOperationException>(
            () => QuerySegmentTokens.Substitute(Ranged, segment));

        Assert.Contains(segment.Describe(), ex.Message);
    }
}
