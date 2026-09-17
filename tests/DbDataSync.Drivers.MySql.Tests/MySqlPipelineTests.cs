using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using MySqlConnector;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>
/// MySQL/MariaDB → MySQL/MariaDB, to isolate driver bugs from cross-engine ones — the same purpose
/// <c>PostgresPipelineTests</c> serves for Postgres. Every reader/staging/writer here is
/// <c>DbDataSync.Drivers.Generic</c>'s, driven by <see cref="MySqlDialect"/>; this driver registers
/// nothing of its own beyond the trigger-audit DDL exercised separately in
/// <see cref="MySqlFamilyTriggerAuditReaderTestsBase{TFixture}"/>. Run against both
/// <see cref="MySqlTestDatabase"/> and <see cref="MariaDbTestDatabase"/> from one shared body.
/// </summary>
[Trait("Category", "Integration")]
public abstract class MySqlFamilyPipelineTestsBase<TFixture> : IClassFixture<TFixture>, IAsyncLifetime
    where TFixture : MySqlFamilyTestDatabase
{
    private readonly TFixture _db;
    private readonly BatchReloadReader _reader = new(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance);
    private readonly BatchInsertStagingProvider _staging = new(MySqlDialect.Instance, MySqlCatalog.Instance);
    private readonly DeleteInsertWriter _writer = new(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance);
    private readonly WatermarkReader _watermark =
        new(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlValueBinding.Instance);

    private MySqlConnection _source = null!;
    private MySqlConnection _target = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    protected MySqlFamilyPipelineTestsBase(TFixture db) => _db = db;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
        new() { SourceColumn = "modified_at", TargetColumn = "modified_at" },
    ];

    public async Task InitializeAsync()
    {
        _source = _db.OpenConnection();
        _target = _db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"src_{suffix}";
        _targetTable = $"tgt_{suffix}";

        const string columns = "id int primary key, name varchar(100) not null, amount decimal(18,2), modified_at datetime(6)";
        await ExecuteAsync(_source, $"CREATE TABLE `{_sourceTable}` ({columns}) ENGINE=InnoDB;");
        await ExecuteAsync(_target, $"CREATE TABLE `{_targetTable}` ({columns}) ENGINE=InnoDB;");
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = _sourceTable };

    private TableRef Target() =>
        new() { ConnectionName = "tgt", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = _targetTable };

    private const string MappingName = "mysql-pipeline";

    private static List<CachedColumn> Columns() =>
    [
        new("id", "int", false, true, false),
        new("name", "varchar(100)", false, false, false),
        new("amount", "decimal(18,2)", true, false, false),
        new("modified_at", "datetime(6)", true, false, false),
    ];

    private async Task<long> ReloadAsync(IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        var read = await _reader.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), options, CancellationToken.None);
        var staged = await _staging.StageAsync(
            _target, Target(), read.Rows, Mappings, MappingName, Columns(), options, CancellationToken.None);
        try
        {
            return (await _writer.ApplyAsync(
                _target, Target(), staged, Mappings, MappingName, Columns(), options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await _staging.CleanupAsync(_target, staged, CancellationToken.None);
        }
    }

    private async Task<Dictionary<int, string>> TargetRowsAsync()
    {
        await using var cmd = _target.CreateCommand();
        cmd.CommandText = $"SELECT id, name FROM `{_targetTable}`;";
        var rows = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[reader.GetInt32(0)] = reader.GetString(1);
        return rows;
    }

    private static Dictionary<string, string> SegmentOption(BatchReloadSegment segment) =>
        new() { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

    [Fact]
    public async Task FullReload_LandsEveryRow()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO `{_sourceTable}` VALUES
                (1, 'Alice', 10.50, '2026-01-01 09:00:00'),
                (2, 'Bob', 20.25, '2026-01-02 09:00:00');
            """);

        Assert.Equal(2, await ReloadAsync());
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task Reload_RemovesRowsDeletedAtTheSource()
    {
        await ExecuteAsync(_source, $"INSERT INTO `{_sourceTable}` VALUES (1, 'Alice', 1, NOW()), (2, 'Bob', 2, NOW());");
        await ReloadAsync();

        await ExecuteAsync(_source, $"DELETE FROM `{_sourceTable}` WHERE id = 2;");
        await ReloadAsync();

        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task SegmentedReload_TouchesOnlyItsOwnRange()
    {
        await ExecuteAsync(_source, $"INSERT INTO `{_sourceTable}` VALUES (1, 'a', 1, NOW()), (2, 'b', 2, NOW()), (9, 'i', 9, NOW());");
        await ReloadAsync();

        await ExecuteAsync(_target, $"UPDATE `{_targetTable}` SET name = 'untouched' WHERE id = 9;");
        await ReloadAsync(SegmentOption(new RangeSegment("id", "1", "3")));

        Assert.Equal("untouched", (await TargetRowsAsync())[9]);
    }

    [Fact]
    public async Task ListSegment_ScopesByValue()
    {
        await ExecuteAsync(_source, $"INSERT INTO `{_sourceTable}` VALUES (1, 'a', 1, NOW()), (2, 'b', 2, NOW()), (3, 'c', 3, NOW());");

        Assert.Equal(2, await ReloadAsync(SegmentOption(new ListSegment("id", ["1", "3"]))));
        Assert.Equal([1, 3], (await TargetRowsAsync()).Keys.Order());
    }

    [Fact]
    public async Task AutoSegments_TileTheRangeSoEveryRowLandsExactlyOnce()
    {
        // No generate_series on either engine; a recursive CTE is the portable substitute both MySQL
        // 8.0+ and MariaDB 10.2.2+ support.
        await ExecuteAsync(_source, $"""
            INSERT INTO `{_sourceTable}`
            WITH RECURSIVE seq AS (
                SELECT 1 AS i
                UNION ALL
                SELECT i + 1 FROM seq WHERE i < 100
            )
            SELECT i, CONCAT('row', i), i, NOW() FROM seq;
            """);

        var expanded = await _reader.ExpandAutoSegmentsAsync(_source, Source(), [new AutoSegment("id", 4)], CancellationToken.None);
        Assert.Equal(4, expanded.Count);

        long total = 0;
        foreach (var segment in expanded)
            total += await ReloadAsync(SegmentOption(segment));

        Assert.Equal(100, total);
        Assert.Equal(100, (await TargetRowsAsync()).Count);
    }

    [Fact]
    public async Task Watermark_ReadsOnlyRowsPastThePreviousMark()
    {
        await ExecuteAsync(_source, $"INSERT INTO `{_sourceTable}` VALUES (1, 'a', 1, '2026-01-01 00:00:00');");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "modified_at" };

        var first = await _watermark.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), options, CancellationToken.None);
        Assert.Single(await CollectAsync(first.Rows));

        await ExecuteAsync(_source, $"INSERT INTO `{_sourceTable}` VALUES (2, 'b', 2, '2026-02-01 00:00:00');");

        var second = await _watermark.ReadChangesAsync(
            _source, Source(), first.NewWatermark, ReadIntent.Changes, Mappings, MappingName, Columns(), options, CancellationToken.None);
        var rows = await CollectAsync(second.Rows);

        Assert.Equal(2, (int)Assert.Single(rows)["id"]!);
    }

    /// <summary>
    /// The absence of the problem <c>PostgresPipelineTests.GeneratedAlwaysIdentity_...</c> exists to
    /// prove a fix for. MySQL accepts an explicit value for an AUTO_INCREMENT column through a plain
    /// INSERT with no session flag and no statement-level override clause — <see cref="SqlDialect"/>'s
    /// own doc comment says so ("MySQL needs nothing"); this is that claim checked against a real
    /// server rather than only asserted.
    /// </summary>
    [Fact]
    public async Task ExplicitAutoIncrementValues_AreWrittenWithNoSpecialHandling()
    {
        var table = $"ident_{Guid.NewGuid():N}";
        await ExecuteAsync(_source, $"CREATE TABLE `{table}` (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50)) ENGINE=InnoDB;");
        await ExecuteAsync(_target, $"CREATE TABLE `{table}_t` (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50)) ENGINE=InnoDB;");
        await ExecuteAsync(_source, $"INSERT INTO `{table}` (id, name) VALUES (5, 'five'), (9, 'nine');");

        var columns = await MySqlCatalog.Instance.GetColumnsAsync(_target, _db.DatabaseName, $"{table}_t", CancellationToken.None);
        Assert.True(columns.Single(c => c.Name == "id").IsIdentity);

        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "id", TargetColumn = "id" },
            new() { SourceColumn = "name", TargetColumn = "name" },
        };
        var src = new SourceTableRef { ConnectionName = "src", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = table };
        var tgt = new TableRef { ConnectionName = "tgt", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = $"{table}_t" };
        var options = new Dictionary<string, string>();
        List<CachedColumn> targetColumns =
        [
            new("id", "int", false, true, true),
            new("name", "varchar(50)", true, false, false),
        ];

        var read = await _reader.ReadChangesAsync(_source, src, null, ReadIntent.InitialLoad, mappings, MappingName, [], options, CancellationToken.None);
        var staged = await _staging.StageAsync(
            _target, tgt, read.Rows, mappings, MappingName, targetColumns, options, CancellationToken.None);
        var written = await _writer.ApplyAsync(
            _target, tgt, staged, mappings, MappingName, targetColumns, options, CancellationToken.None);
        await _staging.CleanupAsync(_target, staged, CancellationToken.None);

        Assert.Equal(2, written.RowsWritten);
    }

    /// <summary>
    /// The opposite of Postgres's own <c>ChangingDatabase_IsRejectedRatherThanSilentlyReconnecting</c> —
    /// confirmed against MySqlConnector's real <c>MySqlConnection.ChangeDatabase</c> override (see
    /// <see cref="MySqlDialect"/>'s own doc comment) rather than assumed. This is exactly the divergence
    /// worth having a test for: the same hook, opposite behaviour, on purpose.
    /// </summary>
    [Fact]
    public async Task ChangingDatabase_ActuallySwitchesTheConnection()
    {
        var other = $"dbdatasync_test_{Guid.NewGuid():N}";
        await ExecuteAsync(_source, $"CREATE DATABASE `{other}`;");
        try
        {
            await MySqlDialect.Instance.UseDatabaseAsync(_source, other, CancellationToken.None);
            Assert.Equal(other, _source.Database);
        }
        finally
        {
            await MySqlDialect.Instance.UseDatabaseAsync(_source, _db.DatabaseName, CancellationToken.None);
            await ExecuteAsync(_source, $"DROP DATABASE IF EXISTS `{other}`;");
        }
    }

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }
}

public sealed class MySqlPipelineTests(MySqlTestDatabase db) : MySqlFamilyPipelineTestsBase<MySqlTestDatabase>(db);

public sealed class MariaDbPipelineTests(MariaDbTestDatabase db) : MySqlFamilyPipelineTestsBase<MariaDbTestDatabase>(db);
