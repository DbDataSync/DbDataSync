using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.DuckDb;

namespace DataSync.Drivers.DuckDb.Tests;

/// <summary>
/// The reader, against a real DuckDB.
/// <para>
/// **Not tagged Integration, deliberately.** Every other driver's end-to-end tests need a server
/// somebody started, which is the entire reason that category exists. DuckDB is a library: an
/// in-memory database costs a few milliseconds and no setup, so these run in the ordinary suite where
/// they will actually be run.
/// </para>
/// </summary>
public sealed class DuckDbQueryReaderTests
{
    private static readonly SourceTableRef Source = new()
    {
        // Left blank throughout: a query-first source names no table, and a test that filled these in
        // would be asserting against a shape the reader is designed not to use.
        ConnectionName = "duck", Database = "", Schema = "", Table = "",
    };

    private static async Task<DbConnection> OpenAsync()
    {
        var connection = new DuckDbDriver().CreateConnection(
            new ConnectionConfig { Name = "duck", DriverType = ConnectionDriverType.DuckDb, AuthMode = AuthMode.None },
            credential: null);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<(IReadOnlyList<ChangeRow> Rows, ReadResult Result)> ReadAsync(
        DbConnection connection, Dictionary<string, string> options, string? previousWatermark = null)
    {
        var result = await new DuckDbQueryReader().ReadChangesAsync(
            connection, Source, previousWatermark, [], "mapping", [], options, CancellationToken.None);

        var rows = new List<ChangeRow>();
        await foreach (var row in result.Rows)
            rows.Add(row);

        return (rows, result);
    }

    private static Dictionary<string, string> Options(string query, BatchReloadSegment? segment = null)
    {
        var options = new Dictionary<string, string> { [DuckDbQueryReader.QueryOption] = query };
        if (segment is not null)
            options[SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment);
        return options;
    }

    [Fact]
    public async Task ReadsTheQuerysOwnResultColumns()
    {
        await using var connection = await OpenAsync();

        var (rows, _) = await ReadAsync(connection, Options("SELECT 1 AS Id, 'a' AS Name"));

        var row = Assert.Single(rows);
        Assert.Equal(["Id", "Name"], row.Schema.ColumnNames);
        Assert.Equal(1, Convert.ToInt32(row[0]));
        Assert.Equal("a", row[1]);
    }

    /// <summary>A scan observes what exists and nothing else — the reload contract, so a reconciling
    /// writer is what removes target rows this did not produce.</summary>
    [Fact]
    public async Task EveryRowIsAnInsert()
    {
        await using var connection = await OpenAsync();

        var (rows, _) = await ReadAsync(connection, Options(
            "SELECT * FROM (VALUES (1), (2), (3)) AS t(Id)"));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
    }

    /// <summary>
    /// The <see cref="BatchReloadReader"/> contract: never interpreted, never invented, handed back
    /// exactly as it arrived — so a Primary pass, which does persist what comes back here, leaves the
    /// stored watermark as it found it.
    /// </summary>
    [Theory]
    [InlineData("0x00000024")]
    [InlineData("")]
    public async Task PreviousWatermark_IsEchoedBackUnchanged(string previous)
    {
        await using var connection = await OpenAsync();

        var (_, result) = await ReadAsync(connection, Options("SELECT 1 AS Id"), previous);

        Assert.Equal(previous, result.NewWatermark);
        Assert.Equal(previous, result.WatermarkAfterRead);
    }

    /// <summary>No previous watermark becomes the empty string, not null and not a value of this
    /// reader's own devising.</summary>
    [Fact]
    public async Task NoPreviousWatermark_ReportsEmpty()
    {
        await using var connection = await OpenAsync();

        var (_, result) = await ReadAsync(connection, Options("SELECT 1 AS Id"));

        Assert.Equal("", result.NewWatermark);
    }

    /// <summary>
    /// End to end, through a real engine: the substituted literals have to *compare* correctly against
    /// a typed column, not merely appear in the right place in the text. DuckDB casts a string literal
    /// to the column's own type, which is what lets this reader segment without asking a catalog what
    /// the column is.
    /// </summary>
    [Fact]
    public async Task RangeSegment_NarrowsTheRowsTheQueryReturns()
    {
        await using var connection = await OpenAsync();

        var (rows, _) = await ReadAsync(connection, Options(
            "SELECT Id FROM (VALUES (1), (5), (9)) AS t(Id) " +
            "WHERE {{segmentColumn}} >= {{segmentMin}} AND {{segmentColumn}} < {{segmentMax}}",
            new RangeSegment("Id", "2", "8")));

        Assert.Equal([5], rows.Select(r => Convert.ToInt32(r[0])));
    }

    [Fact]
    public async Task ListSegment_NarrowsTheRowsTheQueryReturns()
    {
        await using var connection = await OpenAsync();

        var (rows, _) = await ReadAsync(connection, Options(
            "SELECT Region FROM (VALUES ('EMEA'), ('APAC'), ('AMER')) AS t(Region) " +
            "WHERE {{segmentColumn}} IN ({{segmentValues}})",
            new ListSegment("Region", ["EMEA", "AMER"])));

        Assert.Equal(["EMEA", "AMER"], rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task AnUnsegmentedRead_RunsTheQueryWhole()
    {
        await using var connection = await OpenAsync();

        var (rows, _) = await ReadAsync(connection, Options(
            "SELECT Id FROM (VALUES (1), (5), (9)) AS t(Id)", new FullSegment()));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task AMissingQueryOption_SaysSoRatherThanRunningNothing()
    {
        await using var connection = await OpenAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DuckDbQueryReader().ReadChangesAsync(
                connection, Source, null, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains(DuckDbQueryReader.QueryOption, ex.Message);
    }

    // ---- IStatementPreview ----

    [Fact]
    public async Task DescribeAsync_ShowsTheSubstitutedQueryForASegment()
    {
        await using var connection = await OpenAsync();

        var statement = Assert.Single(await Describe(connection, Options(
            "SELECT * FROM t WHERE {{segmentColumn}} >= {{segmentMin}} AND {{segmentColumn}} < {{segmentMax}}",
            new RangeSegment("Id", "1", "9"))));

        Assert.Equal("SELECT * FROM t WHERE \"Id\" >= '1' AND \"Id\" < '9'", statement.Sql);
        // The operator's statement, not one this reader composed — which is what the origin says.
        Assert.Equal(PreviewOrigin.OperatorSql, statement.Origin);
        Assert.Contains("Id [1, 9)", statement.Title);
    }

    [Fact]
    public async Task DescribeAsync_ShowsTheTokensAsWrittenWhenUnsegmented()
    {
        await using var connection = await OpenAsync();
        const string query = "SELECT * FROM t WHERE {{segmentColumn}} >= {{segmentMin}}";

        var statement = Assert.Single(await Describe(connection, Options(query)));

        Assert.Equal(query, statement.Sql);
        Assert.Contains("Unsegmented", statement.Detail);
    }

    private static async Task<IReadOnlyList<PreviewStatement>> Describe(
        DbConnection connection, IReadOnlyDictionary<string, string> options) =>
        await new DuckDbQueryReader().DescribeAsync(
            new PreviewRequest(
                connection, Source,
                new TableRef { ConnectionName = "t", Database = "", Schema = "", Table = "" },
                [], options, PreviousWatermark: null),
            CancellationToken.None);
}
