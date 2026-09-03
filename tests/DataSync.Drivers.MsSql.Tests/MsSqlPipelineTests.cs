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

    private const string MappingName = "mssql-pipeline";

    /// <summary>MsSqlMergeWriter resolves its target shape from this cache as of phase 91 — matching
    /// Target()'s own CREATE TABLE above, since MsSqlTargetShape.FromCachedColumns needs the target's
    /// full column list, not just the mapped ones.</summary>
    private static List<CachedColumn> TargetColumns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "nvarchar(50)", false, false, false),
        new("Amount", "decimal(18,2)", false, false, false),
    ];

    private async Task<(long RowsWritten, string Watermark)> RunOnceAsync(
        string? previousWatermark, IReadOnlyDictionary<string, string>? writerOptions = null)
    {
        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(), previousWatermark, Mappings, MappingName, [], new Dictionary<string, string>(),
            CancellationToken.None);
        var staged = await _staging.StageAsync(
            _targetConnection, Target(), read.Rows, Mappings, MappingName, [], new Dictionary<string, string>(),
            CancellationToken.None);
        var written = await _writer.ApplyAsync(
            _targetConnection, Target(), staged, Mappings, MappingName, TargetColumns(),
            writerOptions ?? new Dictionary<string, string>(), CancellationToken.None);
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
    public async Task ChunkedApply_AppliesEveryStagedRow_AcrossManyChunks()
    {
        // 250 rows at 10 to a chunk is 25 separate MERGE statements — enough that an off-by-one in the
        // ordinal ranges, or a chunk bound that failed to filter at all, shows up as a wrong count
        // rather than as a coincidence.
        await ExecuteAsync(_sourceConnection, $"""
            WITH Numbers AS (SELECT TOP (250) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                             FROM sys.all_objects)
            INSERT INTO dbo.[{_sourceTable}] (Id, Name, Amount)
            SELECT n, CONCAT('Name', n), n * 1.5 FROM Numbers;
            """);

        var options = new Dictionary<string, string> { ["applyBatchSize"] = "10" };
        var (written, watermark) = await RunOnceAsync(null, options);

        Assert.Equal(250, written);
        var rows = await GetTargetRowsAsync();
        Assert.Equal(250, rows.Count);
        Assert.Equal("Name1", rows[1]);
        Assert.Equal("Name250", rows[250]);

        // And the chunked path stays correct for the incremental shape too, where a chunk can carry a
        // mix of inserts, updates and deletes rather than 250 of one thing.
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Changed' WHERE Id % 5 = 0;");
        await ExecuteAsync(_sourceConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id % 25 = 0;");

        await RunOnceAsync(watermark, options);

        var after = await GetTargetRowsAsync();
        Assert.Equal(240, after.Count);
        Assert.False(after.ContainsKey(25));
        Assert.Equal("Changed", after[5]);
        Assert.Equal("Name4", after[4]);
    }

    [Fact]
    public async Task ChunkedApply_AndTheUnchunkedStatement_ReachTheSameEndState()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name, Amount) VALUES (1, 'Alice', 10.50), (2, 'Bob', 20.00);");

        // applyBatchSize 0 is how an operator asks for the single statement this writer issued before
        // chunking existed — the one escape hatch the option has to keep working.
        var (written, _) = await RunOnceAsync(null, new Dictionary<string, string> { ["applyBatchSize"] = "0" });

        Assert.Equal(2, written);
        Assert.Equal(2, (await GetTargetRowsAsync()).Count);
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
