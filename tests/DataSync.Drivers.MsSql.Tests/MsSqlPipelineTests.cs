using DataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// Exercises the full read -> stage -> apply pipeline manually (DataSync.TaskRunner, which will
/// orchestrate this for real, doesn't exist until Phase 4) to prove the three MSSQL components work
/// together, not just in isolation. Uses two separate connections (source, target) even though both
/// point at the same test database — see IChangeReader's XML doc: sharing one connection between a
/// streaming source read and a target-side SqlBulkCopy deadlocks, even under MARS.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlPipelineTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlChangeTrackingReader _reader = new();
    private readonly MsSqlStagingTableProvider _staging = new();
    private readonly MsSqlMergeWriter _writer = new();
    private SqlConnection _sourceConnection = null!;
    private SqlConnection _targetConnection = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
        new() { SourceColumn = "Amount", TargetColumn = "Amount" },
    ];

    public async Task InitializeAsync()
    {
        _sourceConnection = db.OpenConnection();
        _targetConnection = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"PipelineSrc_{suffix}";
        _targetTable = $"PipelineTgt_{suffix}";

        await ExecuteAsync(_sourceConnection, $"""
            CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(18,2) NOT NULL);
            """);
        await ExecuteAsync(_sourceConnection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(18,2) NOT NULL);
            """);
    }

    public Task DisposeAsync()
    {
        _sourceConnection.Dispose();
        _targetConnection.Dispose();
        return Task.CompletedTask;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = _sourceTable };
    private TableRef Target() => new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable };

    private async Task<(long RowsWritten, string Watermark)> RunOnceAsync(string? previousWatermark)
    {
        var read = await _reader.ReadChangesAsync(_sourceConnection, Source(), previousWatermark, new Dictionary<string, string>(), CancellationToken.None);
        var staged = await _staging.StageAsync(_targetConnection, Target(), read.Rows, Mappings, new Dictionary<string, string>(), CancellationToken.None);
        var written = await _writer.ApplyAsync(_targetConnection, Target(), staged, Mappings, new Dictionary<string, string>(), CancellationToken.None);
        return (written.RowsWritten, read.NewWatermark);
    }

    private async Task<Dictionary<int, string>> GetTargetRowsAsync()
    {
        await using var cmd = _targetConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Name FROM dbo.[{_targetTable}];";
        var results = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results[reader.GetInt32(0)] = reader.GetString(1);
        return results;
    }

    [Fact]
    public async Task FullLoad_ThenIncrementalChanges_ReplicatesCorrectly()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name, Amount) VALUES (1, 'Alice', 10.50), (2, 'Bob', 20.00);");

        var (written1, watermark1) = await RunOnceAsync(null);
        Assert.Equal(2, written1);

        var afterFullLoad = await GetTargetRowsAsync();
        Assert.Equal(2, afterFullLoad.Count);
        Assert.Equal("Alice", afterFullLoad[1]);
        Assert.Equal("Bob", afterFullLoad[2]);

        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name, Amount) VALUES (3, 'Carol', 30.00);");
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Robert', Amount = 25.00 WHERE Id = 2;");
        await ExecuteAsync(_sourceConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 1;");

        var (written2, _) = await RunOnceAsync(watermark1);
        Assert.Equal(3, written2); // 1 insert + 1 update + 1 delete = 3 MERGE-affected rows

        var finalRows = await GetTargetRowsAsync();
        Assert.Equal(2, finalRows.Count);
        Assert.False(finalRows.ContainsKey(1));
        Assert.Equal("Robert", finalRows[2]);
        Assert.Equal("Carol", finalRows[3]);
    }

    [Fact]
    public async Task Incremental_WithNoSourceChanges_WritesNothing()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name, Amount) VALUES (1, 'Alice', 10.50);");
        var (_, watermark) = await RunOnceAsync(null);

        var (written, _) = await RunOnceAsync(watermark);

        Assert.Equal(0, written);
    }
}
