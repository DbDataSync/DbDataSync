using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Npgsql;
using Xunit;
using DataSync.Core.Sql;

namespace DataSync.Drivers.Postgres.Tests;

/// <summary>
/// Postgres → Postgres, to isolate driver bugs from cross-engine ones. Every component here is
/// <c>DataSync.Drivers.Generic</c>'s, driven by <see cref="PostgresDialect"/> — this driver registers
/// nothing of its own.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresPipelineTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance);
    private readonly BatchInsertStagingProvider _staging = new(PostgresDialect.Instance, PostgresCatalog.Instance);
    private readonly DeleteInsertWriter _writer = new(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance);
    private readonly WatermarkReader _watermark =
        new(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance);

    private NpgsqlConnection _source = null!;
    private NpgsqlConnection _target = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
        new() { SourceColumn = "modified_at", TargetColumn = "modified_at" },
    ];

    public async Task InitializeAsync()
    {
        _source = db.OpenConnection();
        _target = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"src_{suffix}";
        _targetTable = $"tgt_{suffix}";

        const string columns = "id integer primary key, name text not null, amount numeric(18,2), modified_at timestamp";
        await ExecuteAsync(_source, $"CREATE TABLE public.\"{_sourceTable}\" ({columns});");
        await ExecuteAsync(_target, $"CREATE TABLE public.\"{_targetTable}\" ({columns});");
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _sourceTable };

    private TableRef Target() =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = _targetTable };

    private async Task<long> ReloadAsync(IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        var read = await _reader.ReadChangesAsync(_source, Source(), null, Mappings, options, CancellationToken.None);
        var staged = await _staging.StageAsync(_target, Target(), read.Rows, Mappings, options, CancellationToken.None);
        try
        {
            return (await _writer.ApplyAsync(_target, Target(), staged, Mappings, options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await _staging.CleanupAsync(_target, staged, CancellationToken.None);
        }
    }

    private async Task<Dictionary<int, string>> TargetRowsAsync()
    {
        await using var cmd = _target.CreateCommand();
        cmd.CommandText = $"SELECT id, name FROM public.\"{_targetTable}\";";
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
            INSERT INTO public."{_sourceTable}" VALUES
                (1, 'Alice', 10.50, '2026-01-01 09:00:00'),
                (2, 'Bob', 20.25, '2026-01-02 09:00:00');
            """);

        Assert.Equal(2, await ReloadAsync());
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task Reload_RemovesRowsDeletedAtTheSource()
    {
        await ExecuteAsync(_source, $"INSERT INTO public.\"{_sourceTable}\" VALUES (1, 'Alice', 1, now()), (2, 'Bob', 2, now());");
        await ReloadAsync();

        await ExecuteAsync(_source, $"DELETE FROM public.\"{_sourceTable}\" WHERE id = 2;");
        await ReloadAsync();

        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task SegmentedReload_TouchesOnlyItsOwnRange()
    {
        await ExecuteAsync(_source, $"INSERT INTO public.\"{_sourceTable}\" VALUES (1, 'a', 1, now()), (2, 'b', 2, now()), (9, 'i', 9, now());");
        await ReloadAsync();

        await ExecuteAsync(_target, $"UPDATE public.\"{_targetTable}\" SET name = 'untouched' WHERE id = 9;");
        await ReloadAsync(SegmentOption(new RangeSegment("id", "1", "3")));

        Assert.Equal("untouched", (await TargetRowsAsync())[9]);
    }

    [Fact]
    public async Task ListSegment_ScopesByValue()
    {
        await ExecuteAsync(_source, $"INSERT INTO public.\"{_sourceTable}\" VALUES (1, 'a', 1, now()), (2, 'b', 2, now()), (3, 'c', 3, now());");

        Assert.Equal(2, await ReloadAsync(SegmentOption(new ListSegment("id", ["1", "3"]))));
        Assert.Equal([1, 3], (await TargetRowsAsync()).Keys.Order());
    }

    [Fact]
    public async Task AutoSegments_TileTheRangeSoEveryRowLandsExactlyOnce()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO public."{_sourceTable}"
            SELECT i, 'row' || i, i, now() FROM generate_series(1, 100) AS i;
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
        await ExecuteAsync(_source, $"INSERT INTO public.\"{_sourceTable}\" VALUES (1, 'a', 1, '2026-01-01 00:00:00');");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "modified_at" };

        var first = await _watermark.ReadChangesAsync(_source, Source(), null, Mappings, options, CancellationToken.None);
        Assert.Single(await CollectAsync(first.Rows));

        await ExecuteAsync(_source, $"INSERT INTO public.\"{_sourceTable}\" VALUES (2, 'b', 2, '2026-02-01 00:00:00');");

        var second = await _watermark.ReadChangesAsync(_source, Source(), first.NewWatermark, Mappings, options, CancellationToken.None);
        var rows = await CollectAsync(second.Rows);

        Assert.Equal(2, (int)Assert.Single(rows)["id"]!);
    }

    [Fact]
    public async Task GeneratedAlwaysIdentity_IsWrittenExplicitlyWithTheOverride()
    {
        // The hook phase 18 defined, and the reason it is "run this write" on SQL Server but a clause
        // here: without OVERRIDING SYSTEM VALUE Postgres rejects the INSERT outright.
        var table = $"ident_{Guid.NewGuid():N}";
        await ExecuteAsync(_source, $"CREATE TABLE public.\"{table}\" (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text);");
        await ExecuteAsync(_target, $"CREATE TABLE public.\"{table}_t\" (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text);");
        await ExecuteAsync(_source, $"INSERT INTO public.\"{table}\" (name) VALUES ('one'), ('two');");

        var columns = await PostgresCatalog.Instance.GetColumnsAsync(_target, "public", $"{table}_t", CancellationToken.None);
        Assert.True(columns.Single(c => c.Name == "id").IsIdentity);

        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "id", TargetColumn = "id" },
            new() { SourceColumn = "name", TargetColumn = "name" },
        };
        var src = new SourceTableRef { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = table };
        var tgt = new TableRef { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = $"{table}_t" };
        var options = new Dictionary<string, string>();

        var read = await _reader.ReadChangesAsync(_source, src, null, mappings, options, CancellationToken.None);
        var staged = await _staging.StageAsync(_target, tgt, read.Rows, mappings, options, CancellationToken.None);
        var written = await _writer.ApplyAsync(_target, tgt, staged, mappings, options, CancellationToken.None);
        await _staging.CleanupAsync(_target, staged, CancellationToken.None);

        Assert.Equal(2, written.RowsWritten);
    }

    [Fact]
    public async Task ChangingDatabase_IsRejectedRatherThanSilentlyReconnecting()
    {
        // Npgsql's ChangeDatabase closes and reopens, discarding the transaction and any streaming
        // cursor. Saying so is the honest answer; doing it quietly is the dangerous one.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresDialect.Instance.UseDatabaseAsync(_source, "some_other_database", CancellationToken.None));

        Assert.Contains("cannot change database", ex.Message);
    }

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }
}
