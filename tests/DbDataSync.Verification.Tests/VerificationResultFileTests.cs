using DbDataSync.Drivers.Generic;
using DbDataSync.Verification;

namespace DbDataSync.Verification.Tests;

/// <summary>
/// A result has to be readable from the file alone — the API serves it back long after the run, and
/// possibly after the config that produced it has changed. So everything the columns cannot carry goes
/// in the file's own metadata, and these check it survives the trip.
/// </summary>
public sealed class VerificationResultFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-verification-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_root, name);

    /// <summary>
    /// The whole file, a page at a time — which is the only way to read one now. There is deliberately
    /// no read-it-all method in production: a check over a large table produces millions of rows, and
    /// a method whose only correct use is "when you know the file is small" is the one that locked the
    /// UI up.
    /// </summary>
    private static async Task<VerificationResultPage> ReadAllAsync(string path, bool differingOnly = false)
    {
        var rows = new List<VerificationRow>();
        VerificationResultPage page;
        var offset = 0;

        do
        {
            page = await VerificationResultQuery.ReadPageAsync(
                path, offset, VerificationResultQuery.MaxPageSize, differingOnly, CancellationToken.None);
            rows.AddRange(page.Rows);
            offset += page.Rows.Count;
        }
        while (page.Rows.Count > 0);

        return page with { Rows = rows };
    }

    private static VerificationResult Result(params VerificationRow[] rows) => new(
        "rows-by-region",
        ["Region"],
        ["__rows"],
        DifferenceThreshold: 0.01,
        SourceReadAtUtc: new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
        TargetReadAtUtc: new DateTimeOffset(2026, 8, 28, 12, 0, 4, TimeSpan.Zero),
        rows);

    private static VerificationRow Row(
        string group, double? source, double? target, VerificationRowStatus status) =>
        new(
            [group],
            source is null ? null : new Dictionary<string, double> { ["__rows"] = source.Value },
            target is null ? null : new Dictionary<string, double> { ["__rows"] = target.Value },
            source is null || target is null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double> { ["__rows"] = target.Value - source.Value },
            status);

    [Fact]
    public async Task AResult_RoundTripsThroughTheFile()
    {
        var path = Path("result.parquet");
        var written = Result(
            Row("north", 100, 100, VerificationRowStatus.Match),
            Row("south", 50, 47, VerificationRowStatus.Differs));

        await VerificationResultFile.WriteAsync(path, written, CancellationToken.None);
        var read = await ReadAllAsync(path);

        Assert.Equal("rows-by-region", read.CheckName);
        Assert.Equal(["Region"], read.GroupColumns);
        Assert.Equal(["__rows"], read.MeasureColumns);
        Assert.Equal(0.01, read.DifferenceThreshold);

        Assert.Equal(2, read.Rows.Count);
        Assert.Equal(["north"], read.Rows[0].Group);
        Assert.Equal(100, read.Rows[0].Source!["__rows"]);
        Assert.Equal(-3, read.Rows[1].Differences["__rows"]);
        Assert.Equal(VerificationRowStatus.Differs, read.Rows[1].Status);
    }

    /// <summary>
    /// The gap between the two reads is what tells an operator whether a difference is drift or a
    /// defect, so it has to survive being written down — a result that loses it is a result they have
    /// to guess about.
    /// </summary>
    [Fact]
    public async Task BothSidesReadTimes_SurviveSeparately()
    {
        var path = Path("times.parquet");
        await VerificationResultFile.WriteAsync(path, Result(Row("north", 1, 1, VerificationRowStatus.Match)), CancellationToken.None);

        var read = await ReadAllAsync(path);

        Assert.Equal(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero), read.SourceReadAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 12, 0, 4, TimeSpan.Zero), read.TargetReadAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(4), read.ReadGap);
    }

    /// <summary>
    /// A group only one side had has no value for the other, and it comes back absent rather than
    /// zero — a zero there is a number nobody measured.
    /// </summary>
    [Fact]
    public async Task AOneSidedGroup_ComesBackWithNoValueForTheMissingSide()
    {
        var path = Path("one-sided.parquet");
        await VerificationResultFile.WriteAsync(path, Result(
            Row("west", 5, null, VerificationRowStatus.MissingFromTarget),
            Row("east", null, 3, VerificationRowStatus.MissingFromSource)), CancellationToken.None);

        var read = await ReadAllAsync(path);

        Assert.Null(read.Rows[0].Target);
        Assert.Equal(5, read.Rows[0].Source!["__rows"]);
        Assert.Null(read.Rows[1].Source);
        Assert.Equal(3, read.Rows[1].Target!["__rows"]);
    }

    [Fact]
    public async Task AnUngroupedCheck_RoundTripsAsOneRowWithNoGroup()
    {
        var path = Path("ungrouped.parquet");
        var written = new VerificationResult(
            "total-rows", [], ["__rows"], 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new VerificationRow(
                [],
                new Dictionary<string, double> { ["__rows"] = 42 },
                new Dictionary<string, double> { ["__rows"] = 42 },
                new Dictionary<string, double> { ["__rows"] = 0 },
                VerificationRowStatus.Match)]);

        await VerificationResultFile.WriteAsync(path, written, CancellationToken.None);
        var read = await ReadAllAsync(path);

        var row = Assert.Single(read.Rows);
        Assert.Empty(row.Group);
        Assert.Equal(42, row.Source!["__rows"]);
    }

    [Fact]
    public async Task AResultWithNoRows_IsStillAReadableFile()
    {
        var path = Path("empty.parquet");
        await VerificationResultFile.WriteAsync(path, Result(), CancellationToken.None);

        var read = await ReadAllAsync(path);

        Assert.Empty(read.Rows);
        Assert.Equal("rows-by-region", read.CheckName);
        Assert.Equal(0, read.DifferingRows);
    }

    [Fact]
    public async Task MultipleMeasures_KeepTheirOwnColumns()
    {
        var path = Path("measures.parquet");
        var written = new VerificationResult(
            "sums", ["Region"], ["Amount", "Quantity"], 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new VerificationRow(
                ["north"],
                new Dictionary<string, double> { ["Amount"] = 100.5, ["Quantity"] = 5 },
                new Dictionary<string, double> { ["Amount"] = 100.5, ["Quantity"] = 4 },
                new Dictionary<string, double> { ["Amount"] = 0, ["Quantity"] = -1 },
                VerificationRowStatus.Differs)]);

        await VerificationResultFile.WriteAsync(path, written, CancellationToken.None);
        var read = await ReadAllAsync(path);

        var row = Assert.Single(read.Rows);
        Assert.Equal(100.5, row.Source!["Amount"]);
        Assert.Equal(-1, row.Differences["Quantity"]);
        Assert.Equal(0, row.Differences["Amount"]);
    }

    [Fact]
    public async Task DifferingGroups_CountsEverythingThatIsNotAMatch()
    {
        var path = Path("counting.parquet");
        await VerificationResultFile.WriteAsync(path, Result(
            Row("north", 1, 1, VerificationRowStatus.Match),
            Row("south", 2, 3, VerificationRowStatus.Differs),
            Row("west", 5, null, VerificationRowStatus.MissingFromTarget)), CancellationToken.None);

        var read = await ReadAllAsync(path);

        Assert.Equal(2, read.DifferingRows);
    }
}
