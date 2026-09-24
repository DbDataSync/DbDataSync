using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// **The milestone.** SQL Server → PostgreSQL and PostgreSQL → SQL Server, both directions, through
/// the same engine-neutral pipeline with nothing but the dialect, catalog and value binder swapped.
/// <para>
/// This is the first time the abstraction built in phases 17 and 18 does what it was built for. It
/// either works or the abstraction is wrong, which is why the assertions compare actual row contents
/// rather than counts.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossEngineReplicationTests : IClassFixture<MsSqlScratchDatabase>, IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlScratchDatabase _mssqlDb;
    private readonly PostgresTestDatabase _pgDb;

    private SqlConnection _mssql = null!;
    private NpgsqlConnection _pg = null!;
    private readonly string _table = $"xeng_{Guid.NewGuid():N}";

    public CrossEngineReplicationTests(MsSqlScratchDatabase mssqlDb, PostgresTestDatabase pgDb)
    {
        _mssqlDb = mssqlDb;
        _pgDb = pgDb;
    }

    // Named lower-case on both sides. Postgres folds unquoted identifiers to lower case and SQL Server
    // does not care, so a lower-case name is the one spelling that needs no quoting rules to line up —
    // which keeps this test about the pipeline rather than about identifier folding.
    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
        new() { SourceColumn = "modified_at", TargetColumn = "modified_at" },
    ];

    private static readonly (int Id, string Name, decimal Amount, DateTime ModifiedAt)[] Seed =
    [
        (1, "Alice", 10.50m, new DateTime(2026, 1, 1, 9, 0, 0, 500, DateTimeKind.Unspecified)),
        (2, "Bob", -3.25m, new DateTime(2026, 2, 15, 13, 30, 45, DateTimeKind.Unspecified)),
        (3, "Carol — ünïcode", 0m, new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Unspecified)),
    ];

    public async Task InitializeAsync()
    {
        _mssql = _mssqlDb.OpenConnection();
        _pg = _pgDb.OpenConnection();

        await ExecAsync(_mssql, $"""
            CREATE TABLE dbo.[{_table}] (
                id INT NOT NULL PRIMARY KEY,
                name NVARCHAR(100) NOT NULL,
                amount DECIMAL(18,2) NULL,
                modified_at DATETIME2(3) NULL);
            """);
        await ExecAsync(_pg, $"""
            CREATE TABLE public."{_table}" (
                id integer PRIMARY KEY,
                name text NOT NULL,
                amount numeric(18,2) NULL,
                modified_at timestamp(3) NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        await _mssql.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef MsSqlSource() =>
        new() { ConnectionName = "mssql", Database = _mssqlDb.DatabaseName, Schema = "dbo", Table = _table };
    private TableRef MsSqlTarget() =>
        new() { ConnectionName = "mssql", Database = _mssqlDb.DatabaseName, Schema = "dbo", Table = _table };
    private SourceTableRef PgSource() =>
        new() { ConnectionName = "pg", Database = _pgDb.DatabaseName, Schema = "public", Table = _table };
    private TableRef PgTarget() =>
        new() { ConnectionName = "pg", Database = _pgDb.DatabaseName, Schema = "public", Table = _table };

    private sealed record Side(IChangeReader Reader, IStagingProvider Staging, IChangeWriter Writer, IChangeReader Watermark);

    private const string MappingName = "cross-engine";

    /// <summary>Matches InitializeAsync's own MSSQL CREATE TABLE — the generic staging provider and
    /// writer resolve target column shape from here as of phase 91, in the native spelling
    /// MsSqlSchemaQueries.GetColumnsAsync would have returned live.</summary>
    private static List<CachedColumn> MsSqlColumns() =>
    [
        new("id", "int", false, true, false),
        new("name", "nvarchar(100)", false, false, false),
        new("amount", "decimal(18,2)", true, false, false),
        new("modified_at", "datetime2(3)", true, false, false),
    ];

    /// <summary>The same table's Postgres side.</summary>
    private static List<CachedColumn> PgColumns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "text", false, false, false),
        new("amount", "numeric(18,2)", true, false, false),
        new("modified_at", "timestamp(3)", true, false, false),
    ];

    /// <summary>
    /// Resolved from the driver by Kind, exactly as <c>RunExecutor</c> resolves them through
    /// <c>DriverRegistry</c> — rather than constructed here from each driver's dialect and catalog.
    /// Both because those are internal to their drivers (deliberately: a caller has no business
    /// assembling a pipeline by hand) and because a test that built its own would prove the components
    /// work while saying nothing about whether the driver registers them.
    /// </summary>
    private static Side SideOf(IDriver driver) => new(
        driver.Readers.Single(r => r.Kind == GenericDriverKinds.BatchReload),
        driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable),
        driver.Writers.Single(w => w.Kind == GenericDriverKinds.DeleteInsert),
        driver.Readers.Single(r => r.Kind == GenericDriverKinds.Watermark));

    private static Side MsSqlSide() => SideOf(new MsSqlDriver());

    private static Side PgSide() => SideOf(new PostgresDriver());

    private async Task SeedMsSqlAsync()
    {
        foreach (var (id, name, amount, modified) in Seed)
        {
            await using var cmd = _mssql.CreateCommand();
            cmd.CommandText = $"INSERT INTO dbo.[{_table}] (id, name, amount, modified_at) VALUES (@id, @name, @amount, @modified);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@amount", amount);
            cmd.Parameters.AddWithValue("@modified", modified);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedPostgresAsync()
    {
        foreach (var (id, name, amount, modified) in Seed)
        {
            await using var cmd = _pg.CreateCommand();
            cmd.CommandText = $"INSERT INTO public.\"{_table}\" (id, name, amount, modified_at) VALUES (@id, @name, @amount, @modified);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@amount", amount);
            cmd.Parameters.AddWithValue("@modified", modified);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<(int, string, decimal?, DateTime?)>> ReadAllAsync(
        System.Data.Common.DbConnection connection, string qualified)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id, name, amount, modified_at FROM {qualified} ORDER BY id;";
        var rows = new List<(int, string, decimal?, DateTime?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
        }
        return rows;
    }

    [Fact]
    public async Task MsSqlToPostgres_ReloadCarriesEveryValueFaithfully()
    {
        await SeedMsSqlAsync();
        var source = MsSqlSide();
        var target = PgSide();
        var options = new Dictionary<string, string>();

        var read = await source.Reader.ReadChangesAsync(
            _mssql, MsSqlSource(), null, ReadIntent.InitialLoad, Mappings, MappingName, MsSqlColumns(), [], options, CancellationToken.None);
        var staged = await target.Staging.StageAsync(
            _pg, PgTarget(), read.Rows, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
        var written = await target.Writer.ApplyAsync(
            _pg, PgTarget(), staged, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
        await target.Staging.CleanupAsync(_pg, staged, CancellationToken.None);

        Assert.Equal(3, written.RowsWritten);

        // Values, not counts: type mapping is where cross-engine replication actually breaks, and a
        // row count would pass while every decimal was silently truncated.
        var landed = await ReadAllAsync(_pg, $"public.\"{_table}\"");
        Assert.Equal(
            Seed.Select(r => (r.Id, r.Name, (decimal?)r.Amount, (DateTime?)r.ModifiedAt)).ToList(),
            landed);
    }

    [Fact]
    public async Task PostgresToMsSql_ReloadCarriesEveryValueFaithfully()
    {
        await SeedPostgresAsync();
        var source = PgSide();
        var target = MsSqlSide();
        var options = new Dictionary<string, string>();

        var read = await source.Reader.ReadChangesAsync(
            _pg, PgSource(), null, ReadIntent.InitialLoad, Mappings, MappingName, PgColumns(), [], options, CancellationToken.None);
        var staged = await target.Staging.StageAsync(
            _mssql, MsSqlTarget(), read.Rows, Mappings, MappingName, MsSqlColumns(), options, CancellationToken.None);
        var written = await target.Writer.ApplyAsync(
            _mssql, MsSqlTarget(), staged, Mappings, MappingName, MsSqlColumns(), options, CancellationToken.None);
        await target.Staging.CleanupAsync(_mssql, staged, CancellationToken.None);

        Assert.Equal(3, written.RowsWritten);

        var landed = await ReadAllAsync(_mssql, $"dbo.[{_table}]");
        Assert.Equal(
            Seed.Select(r => (r.Id, r.Name, (decimal?)r.Amount, (DateTime?)r.ModifiedAt)).ToList(),
            landed);
    }

    [Fact]
    public async Task MsSqlToPostgres_WatermarkModeReadsOnlyWhatIsNew()
    {
        await SeedMsSqlAsync();
        var source = MsSqlSide();
        var target = PgSide();
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "modified_at" };

        var first = await source.Watermark.ReadChangesAsync(
            _mssql, MsSqlSource(), null, ReadIntent.InitialLoad, Mappings, MappingName, MsSqlColumns(), [], options, CancellationToken.None);
        var staged = await target.Staging.StageAsync(
            _pg, PgTarget(), first.Rows, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
        await target.Writer.ApplyAsync(_pg, PgTarget(), staged, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
        await target.Staging.CleanupAsync(_pg, staged, CancellationToken.None);

        Assert.Equal(3, (await ReadAllAsync(_pg, $"public.\"{_table}\"")).Count);

        // The watermark's sub-second component has to survive the round trip through the work queue's
        // text column, or this row is re-read on every subsequent pass.
        await ExecAsync(_mssql, $"INSERT INTO dbo.[{_table}] VALUES (4, 'Dave', 7.77, '2026-04-01T08:00:00.250');");

        var second = await source.Watermark.ReadChangesAsync(
            _mssql, MsSqlSource(), first.NewWatermark, ReadIntent.Changes, Mappings, MappingName, MsSqlColumns(), [], options, CancellationToken.None);
        var rows = new List<ChangeRow>();
        await foreach (var row in second.Rows)
            rows.Add(row);

        Assert.Equal(4, (int)Assert.Single(rows)["id"]!);
    }

    [Fact]
    public async Task CrossEngineReload_RemovesRowsDeletedAtTheSource()
    {
        await SeedMsSqlAsync();
        var source = MsSqlSide();
        var target = PgSide();
        var options = new Dictionary<string, string>();

        async Task ReloadAsync()
        {
            var read = await source.Reader.ReadChangesAsync(
                _mssql, MsSqlSource(), null, ReadIntent.InitialLoad, Mappings, MappingName, MsSqlColumns(), [], options, CancellationToken.None);
            var staged = await target.Staging.StageAsync(
                _pg, PgTarget(), read.Rows, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
            await target.Writer.ApplyAsync(_pg, PgTarget(), staged, Mappings, MappingName, PgColumns(), options, CancellationToken.None);
            await target.Staging.CleanupAsync(_pg, staged, CancellationToken.None);
        }

        await ReloadAsync();
        await ExecAsync(_mssql, $"DELETE FROM dbo.[{_table}] WHERE id = 2;");
        await ReloadAsync();

        Assert.Equal([1, 3], (await ReadAllAsync(_pg, $"public.\"{_table}\"")).Select(r => r.Item1));
    }
}
