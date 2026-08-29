using DataSync.Drivers.Generic;
using DataSync.Verification;

namespace DataSync.Verification.Tests;

/// <summary>
/// A result is read a page at a time, by querying the parquet with SQL. It used to be read whole —
/// every group deserialised here, serialised into one response, and rendered as a DOM node per cell —
/// which locked the browser up on a check over a large table and crashed some tabs.
/// </summary>
public sealed class VerificationResultQueryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datasync-verification-query-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Writes <paramref name="rows"/> groups, every fourth one differing.</summary>
    private async Task<string> WriteAsync(int rows, string groupColumn = "Region")
    {
        var path = System.IO.Path.Combine(_root, "result.parquet");
        var written = new List<VerificationRow>();

        for (var i = 0; i < rows; i++)
        {
            var differs = i % 4 == 0;
            written.Add(new VerificationRow(
                [$"g{i:D6}"],
                new Dictionary<string, double> { ["__rows"] = 100 },
                new Dictionary<string, double> { ["__rows"] = differs ? 90 : 100 },
                new Dictionary<string, double> { ["__rows"] = differs ? -10 : 0 },
                differs ? VerificationRowStatus.Differs : VerificationRowStatus.Match));
        }

        await VerificationResultFile.WriteAsync(path, new VerificationResult(
            "rows-by-region", [groupColumn], ["__rows"], 0,
            new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 9, 0, 3, TimeSpan.Zero),
            written), CancellationToken.None);

        return path;
    }

    private static Task<VerificationResultPage> PageAsync(
        string path, int offset, int limit, bool differingOnly = false) =>
        VerificationResultQuery.ReadPageAsync(path, offset, limit, differingOnly, CancellationToken.None);

    [Fact]
    public async Task APage_CarriesOnlyWhatWasAskedFor()
    {
        var page = await PageAsync(await WriteAsync(1_000), offset: 0, limit: 25);

        Assert.Equal(25, page.Rows.Count);
        Assert.Equal("g000000", page.Rows[0].Group[0]);
    }

    /// <summary>Page two follows page one. Parquet has no key worth sorting by, so the file's own row
    /// order is the order — and it has to be stable or paging means nothing.</summary>
    [Fact]
    public async Task Offset_PicksUpWhereThePreviousPageStopped()
    {
        var path = await WriteAsync(1_000);

        var first = await PageAsync(path, offset: 0, limit: 10);
        var second = await PageAsync(path, offset: 10, limit: 10);

        Assert.Equal("g000009", first.Rows[^1].Group[0]);
        Assert.Equal("g000010", second.Rows[0].Group[0]);
        Assert.Equal(10, second.Offset);
    }

    /// <summary>
    /// Both counts describe the whole file, not the page — they are what the pager and the summary are
    /// built from, and a count of the page would say the same thing as its length.
    /// </summary>
    [Fact]
    public async Task TheCounts_DescribeTheWholeFile()
    {
        var page = await PageAsync(await WriteAsync(1_000), offset: 0, limit: 10);

        Assert.Equal(1_000, page.TotalRows);
        Assert.Equal(250, page.DifferingRows);
    }

    /// <summary>The reason to look at a result at all is usually the rows that disagree, and on a
    /// large check they are a needle in the other 99%.</summary>
    [Fact]
    public async Task DifferingOnly_FiltersInTheQueryRatherThanAfterIt()
    {
        var page = await PageAsync(await WriteAsync(1_000), offset: 0, limit: 10, differingOnly: true);

        Assert.Equal(10, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.NotEqual(VerificationRowStatus.Match, r.Status));
        // Still the file's counts, so the screen can say "10 of 250 differing, out of 1,000".
        Assert.Equal(1_000, page.TotalRows);
        Assert.Equal(250, page.DifferingRows);
    }

    /// <summary>A client asking for a million rows is asking for the bug this replaced.</summary>
    [Fact]
    public async Task ALimitBeyondTheCap_IsCapped()
    {
        var page = await PageAsync(await WriteAsync(1_000), offset: 0, limit: 100_000);

        Assert.Equal(VerificationResultQuery.MaxPageSize, page.Rows.Count);
    }

    [Fact]
    public async Task AnOffsetPastTheEnd_IsAnEmptyPageRatherThanAnError()
    {
        var page = await PageAsync(await WriteAsync(10), offset: 500, limit: 10);

        Assert.Empty(page.Rows);
        Assert.Equal(10, page.TotalRows);
    }

    /// <summary>
    /// A group column is named by the operator's own mapping. One containing a double quote is legal
    /// and would end the identifier, leaving the rest of the name as SQL.
    /// </summary>
    [Fact]
    public async Task AColumnNameContainingAQuote_IsQuotedRatherThanInterpolated()
    {
        var page = await PageAsync(await WriteAsync(5, groupColumn: "od\"d name"), offset: 0, limit: 5);

        Assert.Equal(["od\"d name"], page.GroupColumns);
        Assert.Equal(5, page.Rows.Count);
    }

    /// <summary>The header travels on every page: a difference is only as meaningful as the gap
    /// between the two reads is small, and page four needs that as much as page one.</summary>
    [Fact]
    public async Task EveryPage_CarriesTheHeader()
    {
        var page = await PageAsync(await WriteAsync(1_000), offset: 900, limit: 10);

        Assert.Equal("rows-by-region", page.CheckName);
        Assert.Equal(["__rows"], page.MeasureColumns);
        Assert.Equal(TimeSpan.FromSeconds(3), page.ReadGap);
    }

    /// <summary>
    /// The point of the exercise: a result far larger than anything a browser could render comes back
    /// in the time a request should take, because only the page is read.
    /// </summary>
    [Fact]
    public async Task ALargeResult_IsPagedWithoutReadingItAll()
    {
        var path = await WriteAsync(200_000);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var page = await PageAsync(path, offset: 150_000, limit: 50);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(50, page.Rows.Count);
        Assert.Equal(200_000, page.TotalRows);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed.TotalSeconds:F1}s to read one page.");
    }
}
