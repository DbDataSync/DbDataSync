using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

[Trait("Category", "Integration")]
public sealed class MsSqlWatermarkReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlWatermarkReader _reader = new();
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
            _reader.ReadChangesAsync(_connection, Source(), null, new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public async Task FullLoad_ReturnsAllRows()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1), (2, 'Bob', 1);");

        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };
        var result = await _reader.ReadChangesAsync(_connection, Source(), null, options, CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", result.NewWatermark);
    }

    [Fact]
    public async Task Incremental_ReturnsOnlyRowsAboveWatermark()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (1, 'Alice', 1), (2, 'Bob', 1);");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "Version" };

        var baseline = await _reader.ReadChangesAsync(_connection, Source(), null, options, CancellationToken.None);
        await CollectAsync(baseline.Rows);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Version) VALUES (3, 'Carol', 2);");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert', Version = 2 WHERE Id = 2;");

        var result = await _reader.ReadChangesAsync(_connection, Source(), baseline.NewWatermark, options, CancellationToken.None);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (int)r.Values["Id"]! == 3 && (string)r.Values["Name"]! == "Carol");
        Assert.Contains(rows, r => (int)r.Values["Id"]! == 2 && (string)r.Values["Name"]! == "Robert");
        Assert.Equal("2", result.NewWatermark);
    }
}
