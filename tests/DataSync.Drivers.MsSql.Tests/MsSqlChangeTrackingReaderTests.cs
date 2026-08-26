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
            _connection, Source(), previousWatermark: null, options: new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
        Assert.Contains(rows, r => (int)r.Values["Id"]! == 1 && (string)r.Values["Name"]! == "Alice");
        Assert.False(string.IsNullOrEmpty(result.NewWatermark));
    }

    [Fact]
    public async Task Incremental_DetectsInsertUpdateAndDelete()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol');");

        var baseline = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);
        var watermark = baseline.NewWatermark;

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (4, 'Dave');");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 3;");

        var result = await _reader.ReadChangesAsync(_connection, Source(), watermark, new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(3, rows.Count);

        var inserted = Assert.Single(rows, r => r.Operation == ChangeOperation.Insert);
        Assert.Equal(4, (int)inserted.Values["Id"]!);
        Assert.Equal("Dave", (string)inserted.Values["Name"]!);

        var updated = Assert.Single(rows, r => r.Operation == ChangeOperation.Update);
        Assert.Equal(2, (int)updated.Values["Id"]!);
        Assert.Equal("Robert", (string)updated.Values["Name"]!);

        var deleted = Assert.Single(rows, r => r.Operation == ChangeOperation.Delete);
        Assert.Equal(3, (int)deleted.Values["Id"]!);
        Assert.False(deleted.Values.ContainsKey("Name"));
    }

    [Fact]
    public async Task Incremental_WithNoChanges_ReturnsEmptyButAdvancesWatermark()
    {
        var baseline = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), baseline.NewWatermark, new Dictionary<string, string>(), CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Empty(rows);
        Assert.Equal(baseline.NewWatermark, result.NewWatermark);
    }
}
