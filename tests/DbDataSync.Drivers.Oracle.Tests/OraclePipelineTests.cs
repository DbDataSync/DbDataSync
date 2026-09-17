using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>
/// Oracle → Oracle, to isolate driver bugs from cross-engine ones — the same purpose
/// <c>PostgresPipelineTests</c>/<c>MySqlPipelineTests</c> serve for their own engines. Every
/// reader/staging/writer here but <see cref="OracleFlashbackReader"/> (tested separately) is
/// <c>DbDataSync.Drivers.Generic</c>'s, driven by <see cref="OracleDialect"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OraclePipelineTests(OracleTestDatabase db) : IClassFixture<OracleTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance);
    private readonly BatchInsertStagingProvider _staging = new(OracleDialect.Instance, OracleCatalog.Instance);
    private readonly DeleteInsertWriter _writer = new(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance);
    private readonly WatermarkReader _watermark =
        new(OracleDialect.Instance, OracleCatalog.Instance, OracleValueBinding.Instance);

    private readonly List<string> _tablesToDrop = [];
    private OracleConnection _source = null!;
    private OracleConnection _target = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    // Uppercase throughout — the driver's own generated SQL quotes these exactly
    // (dialect.QuoteIdentifier), and an unquoted column name in the CREATE TABLE below folds to
    // uppercase, the opposite of Postgres/MySQL's lowercase-folding convention. A quoted lowercase
    // reference against an unquoted-uppercase real column is exactly the ORA-00904 this once produced.
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "ID", TargetColumn = "ID" },
        new() { SourceColumn = "NAME", TargetColumn = "NAME" },
        new() { SourceColumn = "AMOUNT", TargetColumn = "AMOUNT" },
        new() { SourceColumn = "MODIFIED_AT", TargetColumn = "MODIFIED_AT" },
    ];

    public async Task InitializeAsync()
    {
        _source = db.OpenConnection();
        _target = db.OpenConnection();
        // Uppercase: the table name is quoted below, preserving its exact case, but OracleCatalog's
        // own live lookups (ExpandAutoSegmentsAsync's path) uppercase whatever name they're given
        // before querying — matching an unquoted CREATE TABLE's natural fold. A lowercase-hex GUID
        // suffix left as-is would quote to a mixed-case name the catalog's own uppercasing can never
        // find, which is exactly the bug this once was.
        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        _sourceTable = $"SRC_{suffix}";
        _targetTable = $"TGT_{suffix}";

        const string columns = "id number(10,0) primary key, name varchar2(100) not null, amount number(18,2), modified_at timestamp(6)";
        await ExecuteAsync(_source, $"CREATE TABLE \"{_sourceTable}\" ({columns})");
        await ExecuteAsync(_target, $"CREATE TABLE \"{_targetTable}\" ({columns})");
        _tablesToDrop.Add(_sourceTable);
        _tablesToDrop.Add(_targetTable);
    }

    public async Task DisposeAsync()
    {
        foreach (var table in _tablesToDrop)
        {
            try
            {
                await ExecuteAsync(_source, $"DROP TABLE \"{table}\" PURGE");
            }
            catch
            {
                // Best-effort cleanup; a failure here should not fail the test that already ran.
            }
        }
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = "FREEPDB1", Schema = db.SchemaName, Table = _sourceTable };

    private TableRef Target() =>
        new() { ConnectionName = "tgt", Database = "FREEPDB1", Schema = db.SchemaName, Table = _targetTable };

    private const string MappingName = "oracle-pipeline";

    private static List<CachedColumn> Columns() =>
    [
        new("ID", "NUMBER(10,0)", false, true, false),
        new("NAME", "VARCHAR2(100)", false, false, false),
        new("AMOUNT", "NUMBER(18,2)", true, false, false),
        new("MODIFIED_AT", "TIMESTAMP(6)", true, false, false),
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
        cmd.CommandText = $"SELECT id, name FROM \"{_targetTable}\"";
        var rows = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[Convert.ToInt32(reader.GetDecimal(0))] = reader.GetString(1);
        return rows;
    }

    private static Dictionary<string, string> SegmentOption(BatchReloadSegment segment) =>
        new() { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

    [Fact]
    public async Task FullReload_LandsEveryRow()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO "{_sourceTable}" VALUES (1, 'Alice', 10.50, TIMESTAMP '2026-01-01 09:00:00')
            """);
        await ExecuteAsync(_source, $"""
            INSERT INTO "{_sourceTable}" VALUES (2, 'Bob', 20.25, TIMESTAMP '2026-01-02 09:00:00')
            """);

        Assert.Equal(2, await ReloadAsync());
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task Reload_RemovesRowsDeletedAtTheSource()
    {
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (1, 'Alice', 1, SYSTIMESTAMP)");
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (2, 'Bob', 2, SYSTIMESTAMP)");
        await ReloadAsync();

        await ExecuteAsync(_source, $"DELETE FROM \"{_sourceTable}\" WHERE id = 2");
        await ReloadAsync();

        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await TargetRowsAsync());
    }

    [Fact]
    public async Task SegmentedReload_TouchesOnlyItsOwnRange()
    {
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (1, 'a', 1, SYSTIMESTAMP)");
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (2, 'b', 2, SYSTIMESTAMP)");
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (9, 'i', 9, SYSTIMESTAMP)");
        await ReloadAsync();

        await ExecuteAsync(_target, $"UPDATE \"{_targetTable}\" SET name = 'untouched' WHERE id = 9");
        await ReloadAsync(SegmentOption(new RangeSegment("ID", "1", "3")));

        Assert.Equal("untouched", (await TargetRowsAsync())[9]);
    }

    [Fact]
    public async Task ListSegment_ScopesByValue()
    {
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (1, 'a', 1, SYSTIMESTAMP)");
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (2, 'b', 2, SYSTIMESTAMP)");
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (3, 'c', 3, SYSTIMESTAMP)");

        Assert.Equal(2, await ReloadAsync(SegmentOption(new ListSegment("ID", ["1", "3"]))));
        Assert.Equal([1, 3], (await TargetRowsAsync()).Keys.Order());
    }

    [Fact]
    public async Task AutoSegments_TileTheRangeSoEveryRowLandsExactlyOnce()
    {
        // No generate_series on Oracle; CONNECT BY LEVEL is the idiomatic, portable-to-every-Oracle-
        // version substitute (no recursive-CTE version floor to worry about, unlike MySQL/MariaDB).
        await ExecuteAsync(_source, $"""
            INSERT INTO "{_sourceTable}"
            SELECT LEVEL, 'row' || LEVEL, LEVEL, SYSTIMESTAMP FROM dual CONNECT BY LEVEL <= 100
            """);

        var expanded = await _reader.ExpandAutoSegmentsAsync(_source, Source(), [new AutoSegment("ID", 4)], CancellationToken.None);
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
        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (1, 'a', 1, TIMESTAMP '2026-01-01 00:00:00')");
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "MODIFIED_AT" };

        var first = await _watermark.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, MappingName, Columns(), options, CancellationToken.None);
        Assert.Single(await CollectAsync(first.Rows));

        await ExecuteAsync(_source, $"INSERT INTO \"{_sourceTable}\" VALUES (2, 'b', 2, TIMESTAMP '2026-02-01 00:00:00')");

        var second = await _watermark.ReadChangesAsync(
            _source, Source(), first.NewWatermark, ReadIntent.Changes, Mappings, MappingName, Columns(), options, CancellationToken.None);
        var rows = await CollectAsync(second.Rows);

        Assert.Equal(2m, Convert.ToDecimal(Assert.Single(rows)["ID"]));
    }

    /// <summary>
    /// The real, tested finding this phase's retrospective names: a plain <c>GENERATED ALWAYS AS
    /// IDENTITY</c> column rejects an explicit value outright, with no working override — confirmed
    /// directly, not assumed from the ANSI standard or from Postgres's own precedent. A target that
    /// needs explicit-value round-tripping has to be declared <c>GENERATED BY DEFAULT ON NULL AS
    /// IDENTITY</c> instead, which needs no override machinery at all.
    /// </summary>
    [Fact]
    public async Task ExplicitIdentityValues_NeedByDefaultOnNull_GeneratedAlwaysRejectsThemOutright()
    {
        var alwaysTable = $"IDENT_ALWAYS_{Guid.NewGuid():N}";
        await ExecuteAsync(_target, $"CREATE TABLE \"{alwaysTable}\" (id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR2(50))");
        _tablesToDrop.Add(alwaysTable);

        var ex = await Assert.ThrowsAsync<OracleException>(() =>
            ExecuteAsync(_target, $"INSERT INTO \"{alwaysTable}\" (id, name) VALUES (5, 'five')"));
        Assert.Equal(32795, ex.Number);

        var byDefaultTable = $"IDENT_DEFAULT_{Guid.NewGuid():N}";
        await ExecuteAsync(_target, $"CREATE TABLE \"{byDefaultTable}\" (id NUMBER GENERATED BY DEFAULT ON NULL AS IDENTITY PRIMARY KEY, name VARCHAR2(50))");
        _tablesToDrop.Add(byDefaultTable);

        // No override hook needed at all — a plain INSERT with an explicit value just works.
        await ExecuteAsync(_target, $"INSERT INTO \"{byDefaultTable}\" (id, name) VALUES (5, 'five')");

        await using var cmd = _target.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{byDefaultTable}\" WHERE id = 5";
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    /// <summary>Confirmed against a live connection: <see cref="OracleDialect.UseDatabaseAsync"/> is a
    /// genuine no-op, not a validate-and-refuse — see that method's own doc comment for why there is
    /// nothing meaningful to validate against.</summary>
    [Fact]
    public async Task UseDatabaseAsync_IsANoOp_AndNeverThrows() =>
        await OracleDialect.Instance.UseDatabaseAsync(_source, "anything at all", CancellationToken.None);

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }
}
