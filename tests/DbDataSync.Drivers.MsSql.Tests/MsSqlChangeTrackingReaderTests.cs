using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

[Trait("Category", "Integration")]
public sealed class MsSqlChangeTrackingReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlChangeTrackingReader _reader = new();
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"CtProbe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);
        await ExecuteAsync($"ALTER TABLE dbo.[{_tableName}] ENABLE CHANGE_TRACKING;");
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test",
        Database = db.DatabaseName,
        Schema = "dbo",
        Table = _tableName,
    };

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    /// <summary>
    /// The correctness crux phase 134 depends on. This reader no longer full-loads on
    /// <see cref="ReadIntent.InitialLoad"/> — <c>RunExecutor</c> routes that to the Bulk Load pipeline
    /// instead, capturing this reader's position (<see cref="IPositionCapturing.CapturePositionAsync"/>)
    /// before the load ever reads a row. What has to hold for that handover to be correct: a change
    /// committed *after* the position was captured but before the Bulk Load (or anything else) reads
    /// from the table must still arrive on the very next <see cref="ReadIntent.Changes"/> pass. Get the
    /// ordering backwards — capture after the read instead of before it — and this is exactly the
    /// assertion that fails, silently, in production: the row that would go missing.
    /// </summary>
    [Fact]
    public async Task PositionCapturedBeforeARowExists_StillSeesItOnTheNextChangesPass()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(captured.Position));

        // Committed after the capture, before anything reads from the position it returned.
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (3, 'Carol');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), captured.Position, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        var inserted = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Insert, inserted.Operation);
        Assert.Equal(3, (int)inserted["Id"]!);
        Assert.Equal("Carol", (string)inserted["Name"]!);
    }

    /// <summary>
    /// <see cref="ReadIntent.ChangesFromEarliest"/> passes <c>CHANGE_TRACKING_MIN_VALID_VERSION</c>
    /// straight through as <c>@previousVersion</c> — itself a valid version to read from, so unlike CDC
    /// this needs no separate inclusive statement shape. Against a table with nothing pruned yet, the
    /// floor is the version tracking began at, so this reads everything the feed has ever held.
    /// </summary>
    [Fact]
    public async Task ChangesFromEarliest_ReadsEverythingTheFeedStillHolds()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, ReadIntent.ChangesFromEarliest,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
    }

    /// <summary>
    /// <see cref="ReadIntent.ChangesFromLatest"/> adopts the current change-tracking version as the new
    /// position without querying CHANGETABLE at all — a table can be marked already-synced.
    /// </summary>
    [Fact]
    public async Task ChangesFromLatest_AdoptsTheCurrentVersion_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, ReadIntent.ChangesFromLatest,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(result.Rows));

        // Adopted, not merely equal by coincidence: a pass reading from this position afterwards finds
        // nothing behind it.
        var next = await _reader.ReadChangesAsync(
            _connection, Source(), result.NewWatermark, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
    }

    /// <summary>
    /// <see cref="IPositionCapturing.CapturePositionAsync"/> is exactly what
    /// <see cref="ReadIntent.ChangesFromLatest"/> above adopts — <see cref="MsSqlChangeTrackingReader.GetCurrentVersionAsync"/>
    /// through the capability interface rather than a duplicate call — so a pass reading from the
    /// captured position afterwards finds nothing behind it, the same proof <c>ChangesFromLatest</c>'s
    /// own test gives.
    /// </summary>
    [Fact]
    public async Task CapturePositionAsync_ReturnsTheCurrentVersion_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(captured.Position));

        var next = await _reader.ReadChangesAsync(
            _connection, Source(), captured.Position, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
    }

    [Fact]
    public async Task Incremental_DetectsInsertUpdateAndDelete()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol');");

        // A captured position, not a read — this reader no longer full-loads (see
        // PositionCapturedBeforeARowExists_StillSeesItOnTheNextChangesPass), and the current version
        // after the three rows above is exactly the baseline this test wants to read incrementally from.
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var watermark = (await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None)).Position;

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (4, 'Dave');");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 3;");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(3, rows.Count);

        var inserted = Assert.Single(rows, r => r.Operation == ChangeOperation.Insert);
        Assert.Equal(4, (int)inserted["Id"]!);
        Assert.Equal("Dave", (string)inserted["Name"]!);

        var updated = Assert.Single(rows, r => r.Operation == ChangeOperation.Update);
        Assert.Equal(2, (int)updated["Id"]!);
        Assert.Equal("Robert", (string)updated["Name"]!);

        var deleted = Assert.Single(rows, r => r.Operation == ChangeOperation.Delete);
        Assert.Equal(3, (int)deleted["Id"]!);
        // The column is in the schema but was never populated: a deleted row's non-key values are
        // gone from the source, so only the key is meaningful. Writers key off Operation, not off
        // whether a value is present.
        Assert.Null(deleted["Name"]);
    }

    [Fact]
    public async Task Incremental_WithNoChanges_ReturnsEmptyButAdvancesWatermark()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var baselinePosition = (await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None)).Position;

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), baselinePosition, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Empty(rows);
        Assert.Equal(baselinePosition, result.NewWatermark);
    }

    /// <summary>
    /// The same failure CDC raises, from the other mechanism — which is the whole reason
    /// <see cref="PositionExpiredException"/> is shared rather than each reader wording its own. A
    /// version below the table's minimum valid version is a position Change Tracking cannot serve.
    /// </summary>
    [Fact]
    public async Task AVersionBelowTheMinimumValid_IsReportedAsExpired()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");

        // -1 sorts below every version the source could still hold, which is what a version discarded
        // by cleanup looks like from here.
        var problem = await Assert.ThrowsAsync<PositionExpiredException>(() => _reader.ReadChangesAsync(
            _connection, Source(), "-1", ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Equal("Change Tracking", problem.Mechanism);
        Assert.Equal("-1", problem.StoredPosition);
        Assert.Contains("has to be reloaded", problem.Message);
    }

    private static Dictionary<string, string> Bounded(int maxRows) =>
        new() { [BoundedRead.OptionName] = maxRows.ToString() };

    /// <summary>The baseline every bounded test starts from: a first pass with nothing to read, which
    /// leaves the table's current version stored and CHANGETABLE as the only thing consulted after.</summary>
    private async Task<string> BaselineAsync()
    {
        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        return captured.Position;
    }

    [Fact]
    public async Task Bounded_StopsAtTheCap_AndReportsAVersionBelowTheCurrentOne()
    {
        var watermark = await BaselineAsync();

        // Six statements, six versions. A cap of two must record the second of them, not the sixth.
        for (var id = 1; id <= 6; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(2), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.True(long.Parse(result.WatermarkAfterRead) < long.Parse(result.NewWatermark),
            "a bounded pass must record where it stopped, not the window's end");
    }

    [Fact]
    public async Task Bounded_OverSeveralPasses_DeliversEveryChangeExactlyOnce()
    {
        var watermark = await BaselineAsync();
        for (var id = 1; id <= 30; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");

        var seen = new List<int>();
        for (var pass = 0; pass < 20; pass++)
        {
            var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(4), CancellationToken.None);
            var rows = await CollectAsync(result.Rows);
            seen.AddRange(rows.Select(r => (int)r["Id"]!));
            watermark = result.WatermarkAfterRead;
            if (rows.Count == 0)
                break;
        }

        Assert.Equal(Enumerable.Range(1, 30), seen.Order());
        Assert.Equal(30, seen.Distinct().Count());
    }

    [Fact]
    public async Task Bounded_NeverSplitsRowsSharingTheBoundaryVersion()
    {
        var watermark = await BaselineAsync();

        // One statement, so all four rows share a single SYS_CHANGE_VERSION. A cap of two lands in the
        // middle of it; WITH TIES has to return all four rather than two, because the version this
        // pass would record is the same one the other two sit at, and the next pass reads strictly
        // above it.
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(2), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(4, rows.Count);

        var next = await _reader.ReadChangesAsync(
            _connection, Source(), result.WatermarkAfterRead, ReadIntent.Changes, [], "mapping", [], Bounded(2), CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
    }

    [Fact]
    public async Task Bounded_WhenTheWindowFitsInTheCap_StillAdvancesToTheWindowsEnd()
    {
        // Not a detail: a table with little traffic that only ever advanced to its last changed row
        // would sit still while Change Tracking's cleanup moved on beneath it, and eventually fail as
        // expired. A pass that read its whole window is entitled to the window's end.
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(100), CancellationToken.None);
        await CollectAsync(result.Rows);

        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);
        Assert.True(long.Parse(result.WatermarkAfterRead) > long.Parse(watermark));
    }

    [Fact]
    public async Task Bounded_KeepsTheVersionColumnOutOfTheRowsItEmits()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(10), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(["Id", "Name"], rows[0].Schema.ColumnNames);
    }

    /// <summary>
    /// The other half of phase 84's change of default. Bounding stopped being opt-in for both log-based
    /// readers at once, not just for CDC: the argument that decided it — the mapping nobody configured
    /// is the one that arrives with an unbounded backlog — is about the operator, not the mechanism.
    /// </summary>
    [Fact]
    public async Task WithNoOptionsAtAll_ThePassIsStillCapped()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");

        // The default cap is far above anything this test writes, so what this proves is that the
        // bounded statement is the one a default-configured mapping now runs, and that it returns the
        // same rows through it. The cap's arithmetic is asserted above against a cap small enough to
        // bite; the default's size is BoundedReadTests' business.
        var result = await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Single(await CollectAsync(result.Rows));

        // Not cut short, so it still advances to the window's end rather than to its last row.
        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);
    }

    /// <summary>Zero, and only zero, is how an operator asks for the uncapped read back.</summary>
    [Fact]
    public async Task Uncapped_ReadsTheWholeWindow()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a'), (2, 'b');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(0), CancellationToken.None);

        Assert.Equal(2, (await CollectAsync(result.Rows)).Count);
        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);
    }

    // ---- The commit time captured beside the version (phase 87) ---------------------------------

    /// <summary>
    /// The read returns not just the version it is about to store but the engine's own time for it,
    /// looked up on the connection this pass already had open. That is what lets a lag report later
    /// be a state-database read: without it, the applied side of every CDC/Change Tracking lag
    /// figure had to be re-asked of the source on each request.
    /// </summary>
    [Fact]
    public async Task AReadCarriesTheEnginesCommitTimeForTheVersionItIsAboutToStore()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(result.Rows);

        // Not merely non-null: it has to be a time dm_tran_commit_table actually stated for this
        // version, and the independent lookup of the same version is the only thing that proves the
        // reader asked about the position it is storing rather than some other one.
        Assert.NotNull(result.NewWatermarkTimeUtc);
        Assert.Equal(
            await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
                _connection, long.Parse(result.WatermarkAfterRead), CancellationToken.None),
            result.WatermarkTimeAfterRead);
    }

    /// <summary>
    /// **A capped pass reports the time of the version it reached, not the window's end.** The two
    /// are different positions with different commit times, and the mapping stores the former — a
    /// mapping draining a backlog under a row cap is exactly the one whose lag would otherwise read
    /// as caught-up. See <c>ReadResult.WatermarkTimeAfterRead</c>.
    /// </summary>
    [Fact]
    public async Task ABoundedReadCutShort_CarriesTheTimeOfThePositionItReached()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'b');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes, [], "mapping", [], Bounded(1), CancellationToken.None);
        Assert.Single(await CollectAsync(result.Rows));

        // Cut short, so the stored position is the last row's version — and the time travels with it
        // rather than with the target version the pass did not reach.
        Assert.NotEqual(result.NewWatermark, result.WatermarkAfterRead);
        Assert.NotNull(result.WatermarkTimeAfterRead);
        Assert.Equal(
            await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
                _connection, long.Parse(result.WatermarkAfterRead), CancellationToken.None),
            result.WatermarkTimeAfterRead);
    }
}
