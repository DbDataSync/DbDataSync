using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

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

    [Fact]
    public async Task FullLoad_WhenNoPreviousWatermark_ReturnsAllRowsAsInserts()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, [], options: new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
        Assert.Contains(rows, r => (int)r["Id"]! == 1 && (string)r["Name"]! == "Alice");
        Assert.False(string.IsNullOrEmpty(result.NewWatermark));
    }

    [Fact]
    public async Task Incremental_DetectsInsertUpdateAndDelete()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol');");

        var baseline = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, [], new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);
        var watermark = baseline.NewWatermark;

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (4, 'Dave');");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 3;");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], new Dictionary<string, string>(), CancellationToken.None);
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
        var baseline = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, [], new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), baseline.NewWatermark, [], new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Empty(rows);
        Assert.Equal(baseline.NewWatermark, result.NewWatermark);
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
            _connection, Source(), "-1", [], new Dictionary<string, string>(), CancellationToken.None));

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
        var baseline = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, [], new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);
        return baseline.WatermarkAfterRead;
    }

    [Fact]
    public async Task Bounded_StopsAtTheCap_AndReportsAVersionBelowTheCurrentOne()
    {
        var watermark = await BaselineAsync();

        // Six statements, six versions. A cap of two must record the second of them, not the sixth.
        for (var id = 1; id <= 6; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], Bounded(2), CancellationToken.None);
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
            var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], Bounded(4), CancellationToken.None);
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

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], Bounded(2), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(4, rows.Count);

        var next = await _reader.ReadChangesAsync(
            _connection, Source(), result.WatermarkAfterRead, [], Bounded(2), CancellationToken.None);
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

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], Bounded(100), CancellationToken.None);
        await CollectAsync(result.Rows);

        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);
        Assert.True(long.Parse(result.WatermarkAfterRead) > long.Parse(watermark));
    }

    [Fact]
    public async Task Bounded_KeepsTheVersionColumnOutOfTheRowsItEmits()
    {
        var watermark = await BaselineAsync();
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, [], Bounded(10), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(["Id", "Name"], rows[0].Schema.ColumnNames);
    }
}
