using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

[Trait("Category", "Integration")]
public sealed class MsSqlWatermarkReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    // The reader is engine-neutral; the dialect, catalog and value binder are what make it SQL
    // Server's. Otherwise untouched from when it was MsSqlWatermarkReader — these tests are the proof
    // the move changed no behaviour.
    private readonly WatermarkReader _reader =
        new(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"WatermarkProbe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Version INT NOT NULL
            );
            """);
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

    /// <summary>Phase 91: the reader now reads the watermark column's shape from here rather than a
    /// live catalog call, so every test that reaches past the first "watermarkColumn is required" check
    /// has to hand it a cache matching the table it just created.</summary>
    private const string MappingName = "watermark-probe";

    private static List<CachedColumn> Columns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "nvarchar(50)", false, false, false),
        new("Version", "int", false, false, false),
    ];

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    [Fact]
    public async Task MissingWatermarkOption_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public async Task FullLoad_ReturnsAllRows()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1), (2, 'Bob', 1);");

        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };
        var result = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", result.NewWatermark);
    }

    /// <summary>
    /// <see cref="ReadIntent.ChangesFromLatest"/> adopts <c>MAX(watermarkColumn)</c> as the new position
    /// without reading a row at all — the genuinely useful asymmetric case this reader offers, since for
    /// it the feed *is* the table and <c>ChangesFromEarliest</c> would be a full load under another name
    /// (see <see cref="WatermarkReader.SupportedIntents"/>, which declares one but not the other).
    /// </summary>
    [Fact]
    public async Task ChangesFromLatest_AdoptsTheMaxWatermark_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 5), (2, 'Bob', 5);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.ChangesFromLatest, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        Assert.Empty(await CollectAsync(result.Rows));
        Assert.Equal("5", result.NewWatermark);

        // Adopted, not merely equal by coincidence: an ordinary incremental pass from this position
        // finds nothing behind it.
        var next = await _reader.ReadChangesAsync(
            _connection, Source(), result.NewWatermark, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
    }

    /// <summary>
    /// <see cref="IPositionCapturing.CapturePositionAsync"/> is exactly what
    /// <see cref="ReadIntent.ChangesFromLatest"/> above adopts — <c>MAX(watermarkColumn)</c>, through
    /// the capability interface rather than a duplicated statement.
    /// </summary>
    [Fact]
    public async Task CapturePositionAsync_ReturnsTheMaxWatermark_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 7);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(_connection, Source(), options, CancellationToken.None);

        Assert.Equal("7", captured.Position);

        var next = await _reader.ReadChangesAsync(
            _connection, Source(), captured.Position, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
    }

    [Fact]
    public async Task Incremental_ReturnsOnlyRowsAboveWatermark()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1), (2, 'Bob', 1);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        var baseline = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        await CollectAsync(baseline.Rows);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (3, 'Carol', 2);");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert', Version = 2 WHERE Id = 2;");

        var result = await _reader.ReadChangesAsync(_connection, Source(), baseline.NewWatermark, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (int)r["Id"]! == 3 && (string)r["Name"]! == "Carol");
        Assert.Contains(rows, r => (int)r["Id"]! == 2 && (string)r["Name"]! == "Robert");
        Assert.Equal("2", result.NewWatermark);
    }

    private static Dictionary<string, string> Bounded(int maxRows) => new()
    {
        ["watermarkColumn"] = "Version",
        [BoundedRead.OptionName] = maxRows.ToString(),
    };

    [Fact]
    public async Task Bounded_StopsAtTheCap_AndReportsAWatermarkBelowTheTrueMax()
    {
        // Five rows, five distinct versions, a cap of two. The whole point of the phase: the pass must
        // report version 2 — a position it actually reached — and not version 5, which it did not.
        await ExecuteAsync($"""
            INSERT INTO dbo.[{_tableName}] (Id, Name, Version)
            VALUES (1, 'a', 1), (2, 'b', 2), (3, 'c', 3), (4, 'd', 4), (5, 'e', 5);
            """);

        var result = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(2), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal("2", result.WatermarkAfterRead);
        // The naive cap this design exists to rule out would have said 5 here and lost three rows.
        Assert.NotEqual("5", result.WatermarkAfterRead);
    }

    [Fact]
    public async Task Bounded_OverSeveralPasses_ReadsEveryRowExactlyOnce()
    {
        await ExecuteAsync($"""
            WITH N AS (SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects)
            INSERT INTO dbo.[{_tableName}] (Id, Name, Version) SELECT n, CONCAT('r', n), n FROM N;
            """);

        var seen = new List<int>();
        string? watermark = null;
        for (var pass = 0; pass < 20; pass++)
        {
            var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, watermark is null ? ReadIntent.InitialLoad : ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(7), CancellationToken.None);
            var rows = await CollectAsync(result.Rows);
            seen.AddRange(rows.Select(r => (int)r["Id"]!));
            watermark = result.WatermarkAfterRead;
            if (rows.Count == 0)
                break;
        }

        Assert.Equal(Enumerable.Range(1, 50), seen);
    }

    [Fact]
    public async Task Bounded_NeverSplitsRowsSharingTheBoundaryWatermark()
    {
        // The case the phase names explicitly. Rows 3, 4 and 5 all sit at version 2, and the cap of 3
        // lands exactly on the first of them. WITH TIES has to pull the other two in with it: if it
        // did not, the pass would record version 2 and the next pass — which reads strictly above 2 —
        // would never see rows 4 and 5 again.
        await ExecuteAsync($"""
            INSERT INTO dbo.[{_tableName}] (Id, Name, Version)
            VALUES (1, 'a', 1), (2, 'b', 1), (3, 'c', 2), (4, 'd', 2), (5, 'e', 2), (6, 'f', 3);
            """);

        var first = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(3), CancellationToken.None);
        var firstRows = await CollectAsync(first.Rows);

        // Five rows for a cap of three: the two at version 1, then all three at version 2 rather than
        // just the one that fitted. Over-reading the cap is the correct behaviour here.
        Assert.Equal(5, firstRows.Count);
        Assert.Equal("2", first.WatermarkAfterRead);

        var second = await _reader.ReadChangesAsync(
            _connection, Source(), first.WatermarkAfterRead, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(3), CancellationToken.None);
        var secondRows = await CollectAsync(second.Rows);

        Assert.Equal([6], secondRows.Select(r => (int)r["Id"]!));
        Assert.Equal(
            Enumerable.Range(1, 6),
            firstRows.Concat(secondRows).Select(r => (int)r["Id"]!).Order());
    }

    [Fact]
    public async Task Bounded_KeepsTheWatermarkColumnOutOfTheRowsItEmits()
    {
        // The position column is bookkeeping appended to the select list, not data. A mapped read must
        // not suddenly gain a column nothing asked for.
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'a', 1);");
        List<ColumnMapping> mappings =
        [
            new() { SourceColumn = "Id", TargetColumn = "Id" },
            new() { SourceColumn = "Name", TargetColumn = "Name" },
        ];

        var result = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, mappings, MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(10), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows[0].Schema.Count);
        Assert.Equal("1", result.WatermarkAfterRead);
    }

    [Fact]
    public async Task Bounded_WithNothingToRead_LeavesTheWatermarkWhereItWas()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'a', 1);");
        var first = await _reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(10), CancellationToken.None);
        await CollectAsync(first.Rows);

        var second = await _reader.ReadChangesAsync(
            _connection, Source(), first.WatermarkAfterRead, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), Bounded(10), CancellationToken.None);
        var rows = await CollectAsync(second.Rows);

        Assert.Empty(rows);
        Assert.Equal(first.WatermarkAfterRead, second.WatermarkAfterRead);
    }

    /// <summary>Proves the "zero live catalog calls" and "throws before any live query" halves of
    /// phase 91's contract directly, the same technique phase 87 used for
    /// <c>ReaderLagService</c>/<c>IChangeCounterSource</c>: a fake that throws if
    /// <see cref="ITableCatalog.GetColumnsAsync"/> is ever invoked, wired into a reader instance built
    /// just for these two tests.</summary>
    private sealed class ThrowingTableCatalog : ITableCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
            System.Data.Common.DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "ITableCatalog.GetColumnsAsync was called — phase 91's cache-only readers must never do this.");
    }

    [Fact]
    public async Task Incremental_WithAPopulatedCache_NeverCallsTheLiveCatalog()
    {
        // WatermarkReader holds no ITableCatalog at all as of phase 167V — the guarantee this test
        // exists to pin is now structural, not just runtime-tested.
        var reader = new WatermarkReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        // A first pass never looks the column up at all (no bound to bind yet), so the incremental
        // pass below is the one that would reach a live catalog call if the cache-only path regressed.
        var baseline = await reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        await CollectAsync(baseline.Rows);

        var result = await reader.ReadChangesAsync(
            _connection, Source(), baseline.NewWatermark, ReadIntent.Changes, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        // Reaching here at all is the proof: ThrowingTableCatalog would have failed the test the moment
        // the reader tried the live catalog instead of the cache handed to it.
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Incremental_WithAnEmptyCache_ThrowsMetadataNotCached_AndNeverCallsTheLiveCatalog()
    {
        // WatermarkReader holds no ITableCatalog at all as of phase 167V — the guarantee this test
        // exists to pin is now structural, not just runtime-tested.
        var reader = new WatermarkReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        var baseline = await reader.ReadChangesAsync(_connection, Source(), null, ReadIntent.InitialLoad, [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        await CollectAsync(baseline.Rows);

        // No fallback to ThrowingTableCatalog here either — an empty cache fails loudly on its own,
        // naming the mapping and the side, rather than reaching for a live query.
        var ex = await Assert.ThrowsAsync<MetadataNotCachedException>(() =>
            reader.ReadChangesAsync(_connection, Source(), baseline.NewWatermark, ReadIntent.Changes, [], MappingName, [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None));

        Assert.Equal(MappingName, ex.MappingName);
        Assert.Equal("source", ex.Side);
        // Null, not "Version": an empty cache has no list to look a name up in, so the exception names
        // the side that needs refreshing and stops there. A column name appears only when the cache is
        // populated and that one column is missing from it.
        Assert.Null(ex.Column);
    }
}
