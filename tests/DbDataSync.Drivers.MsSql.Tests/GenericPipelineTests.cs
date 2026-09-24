using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The engine-neutral pipeline — <see cref="BatchReloadReader"/> → <see cref="BatchInsertStagingProvider"/>
/// → <see cref="DeleteInsertWriter"/> — driven by <see cref="MsSqlDialect"/>, against a real server.
/// <para>
/// This is what "prove the generic layer against a working engine" means: none of these three has a
/// line of SQL Server in it, but they land the same rows the MSSQL-specific pipeline does. A new
/// driver that supplies a dialect, a connection factory and a catalog gets exactly this, and this test
/// is the evidence for that claim.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenericPipelineTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
    private readonly BatchInsertStagingProvider _staging = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);
    private readonly DeleteInsertWriter _writer = new(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance);

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
        _sourceTable = $"GenSrc_{suffix}";
        _targetTable = $"GenTgt_{suffix}";

        const string columns = "Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Amount DECIMAL(18,2) NOT NULL";
        await ExecuteAsync(_sourceConnection, $"CREATE TABLE dbo.[{_sourceTable}] ({columns});");
        await ExecuteAsync(_targetConnection, $"CREATE TABLE dbo.[{_targetTable}] ({columns});");
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

    private SourceTableRef Source(string? filter = null) =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = _sourceTable, Filter = filter };

    private TableRef Target() =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable };

    private const string MappingName = "generic-pipeline";

    /// <summary>Matches both Source()'s and Target()'s CREATE TABLE above — phase 91's cache-only
    /// BatchReloadReader/BatchInsertStagingProvider/DeleteInsertWriter all resolve column shape from
    /// here rather than a live catalog call.</summary>
    private static List<CachedColumn> Columns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "nvarchar(50)", false, false, false),
        new("Amount", "decimal(18,2)", false, false, false),
    ];

    private async Task<long> RunOnceAsync(IReadOnlyDictionary<string, string>? options = null, string? filter = null)
    {
        options ??= new Dictionary<string, string>();
        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(filter), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), [], options, CancellationToken.None);
        var staged = await _staging.StageAsync(
            _targetConnection, Target(), read.Rows, Mappings, MappingName, Columns(), options, CancellationToken.None);
        try
        {
            var written = await _writer.ApplyAsync(
                _targetConnection, Target(), staged, Mappings, MappingName, Columns(), options, CancellationToken.None);
            return written.RowsWritten;
        }
        finally
        {
            await _staging.CleanupAsync(_targetConnection, staged, CancellationToken.None);
        }
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

    private static Dictionary<string, string> SegmentOption(BatchReloadSegment segment) =>
        new() { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

    [Fact]
    public async Task FullReload_LandsEveryRow()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 10.50), (2, 'Bob', 20.25), (3, 'Carol', 30.00);
            """);

        var written = await RunOnceAsync();

        Assert.Equal(3, written);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob", [3] = "Carol" }, await GetTargetRowsAsync());
    }

    [Fact]
    public async Task Reload_RemovesRowsDeletedAtTheSource()
    {
        // The reason this writer matters for engines with no change data capture: the reader cannot
        // report a delete, but replacing the scope wholesale converges the target anyway.
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 1), (2, 'Bob', 2);");
        await RunOnceAsync();

        await ExecuteAsync(_sourceConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 2;");
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Alicia' WHERE Id = 1;");
        await RunOnceAsync();

        Assert.Equal(new Dictionary<int, string> { [1] = "Alicia" }, await GetTargetRowsAsync());
    }

    [Fact]
    public async Task SegmentedReload_TouchesOnlyItsOwnRange()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 1), (2, 'Bob', 2), (5, 'Eve', 5);");
        await RunOnceAsync();

        // Change a row outside the segment behind the pipeline's back. A reload of rows 1..3 must not
        // reconcile it away — that is the whole contract a segment makes.
        await ExecuteAsync(_targetConnection, $"UPDATE dbo.[{_targetTable}] SET Name = 'Untouched' WHERE Id = 5;");
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Alicia' WHERE Id = 1;");

        var written = await RunOnceAsync(SegmentOption(new RangeSegment("Id", "1", "3")));

        Assert.Equal(2, written);
        Assert.Equal(
            new Dictionary<int, string> { [1] = "Alicia", [2] = "Bob", [5] = "Untouched" },
            await GetTargetRowsAsync());
    }

    [Fact]
    public async Task Filter_ComposesWithTheSegmentRatherThanBeingReplacedByIt()
    {
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Keep', 1), (2, 'Skip', 2), (3, 'Keep', 3);");

        var written = await RunOnceAsync(SegmentOption(new RangeSegment("Id", "1", "4")), filter: "Name = 'Keep'");

        Assert.Equal(2, written);
        Assert.Equal(new Dictionary<int, string> { [1] = "Keep", [3] = "Keep" }, await GetTargetRowsAsync());
    }

    [Fact]
    public async Task Staging_BatchesWithinTheEnginesParameterLimit()
    {
        // 4 values per row (3 mapped columns + the operation marker) against SQL Server's 2100-parameter
        // cap is 524 rows per statement, so 1500 rows is three statements. A fixed batch size is what
        // made this overflow before the arithmetic was derived from the column count.
        await ExecuteAsync(_sourceConnection, $"""
            WITH n AS (SELECT TOP (1500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a, sys.all_objects b)
            INSERT INTO dbo.[{_sourceTable}] SELECT i, CONCAT('Row', i), i * 1.5 FROM n;
            """);

        var written = await RunOnceAsync();

        Assert.Equal(1500, written);
        Assert.Equal(1500, (await GetTargetRowsAsync()).Count);
    }

    [Fact]
    public async Task Staging_LeavesNoTableBehind()
    {
        // The staging table is real, not a #temp: nothing drops it when the connection closes, so a
        // leak here accumulates tables in the operator's target schema run after run.
        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 1);");
        await RunOnceAsync();

        await using var cmd = _targetConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'DS[_]STG[_]%';";
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AutoSegments_ExpandIntoBucketsCoveringEveryRow()
    {
        await ExecuteAsync(_sourceConnection, $"""
            WITH n AS (SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT INTO dbo.[{_sourceTable}] SELECT i, CONCAT('Row', i), i FROM n;
            """);

        var expanded = await _reader.ExpandAutoSegmentsAsync(
            _sourceConnection, Source(), [new AutoSegment("Id", 4)], Columns(), MappingName, [], CancellationToken.None);

        Assert.Equal(4, expanded.Count);

        long total = 0;
        foreach (var segment in expanded)
            total += await RunOnceAsync(SegmentOption(segment));

        // Every row landed exactly once — buckets tile the range with no gap and no overlap.
        Assert.Equal(100, total);
        Assert.Equal(100, (await GetTargetRowsAsync()).Count);
    }

    /// <summary>Throws if invoked — the same technique phase 87 used to prove <c>ReaderLagService</c>
    /// stopped calling <c>IChangeCounterSource</c>. Wired into a fresh reader/staging/writer trio here so
    /// a regression back to a live catalog call fails the test directly, rather than being inferred from
    /// the pipeline merely having worked.</summary>
    private sealed class ThrowingTableCatalog : ITableCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
            System.Data.Common.DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "ITableCatalog.GetColumnsAsync was called — phase 91's cache-only pipeline must never do this.");
    }

    [Fact]
    public async Task FullReload_WithAPopulatedCache_NeverCallsTheLiveCatalogAnywhereInThePipeline()
    {
        // BatchReloadReader holds no ITableCatalog at all as of phase 167V — the guarantee this test
        // exists to pin is now structural (there's nothing to call), not just runtime-tested. Kept
        // anyway: staging and the writer below still hold one for their own DescribeAsync, unrelated to
        // this reader's ReadChangesAsync.
        var reader = new BatchReloadReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
        var staging = new BatchInsertStagingProvider(MsSqlDialect.Instance, new ThrowingTableCatalog());
        var writer = new DeleteInsertWriter(MsSqlDialect.Instance, new ThrowingTableCatalog(), MsSqlValueBinding.Instance);

        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 10.50);");
        var options = new Dictionary<string, string>();

        var read = await reader.ReadChangesAsync(
            _sourceConnection, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), [], options, CancellationToken.None);
        var staged = await staging.StageAsync(
            _targetConnection, Target(), read.Rows, Mappings, MappingName, Columns(), options, CancellationToken.None);
        try
        {
            var written = await writer.ApplyAsync(
                _targetConnection, Target(), staged, Mappings, MappingName, Columns(), options, CancellationToken.None);

            // Reaching here at all is the proof: any of the three would have thrown
            // InvalidOperationException instead of running to completion if it had reached the catalog.
            Assert.Equal(1, written.RowsWritten);
        }
        finally
        {
            await staging.CleanupAsync(_targetConnection, staged, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StagingWithACacheMissingAMappedColumn_ThrowsMetadataNotCached_NeverReachingTheLiveCatalog()
    {
        var staging = new BatchInsertStagingProvider(MsSqlDialect.Instance, new ThrowingTableCatalog());
        List<CachedColumn> incomplete = [new("Id", "int", false, true, false), new("Name", "nvarchar(50)", false, false, false)];

        await ExecuteAsync(_sourceConnection, $"INSERT INTO dbo.[{_sourceTable}] VALUES (1, 'Alice', 10.50);");
        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), [], new Dictionary<string, string>(), CancellationToken.None);

        // Missing "Amount", which Mappings writes to the target — MetadataNotCachedException, not the
        // ThrowingTableCatalog's InvalidOperationException, is what proves no live query ran first.
        var ex = await Assert.ThrowsAsync<MetadataNotCachedException>(() => staging.StageAsync(
            _targetConnection, Target(), read.Rows, Mappings, MappingName, incomplete, new Dictionary<string, string>(),
            CancellationToken.None));

        Assert.Equal(MappingName, ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Equal("Amount", ex.Column);
    }
}
