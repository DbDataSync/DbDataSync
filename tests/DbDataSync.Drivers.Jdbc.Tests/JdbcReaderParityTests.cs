using System.Globalization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Postgres;
using Npgsql;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 165V's own spike, made concrete: read the same Postgres table through the generic pipeline two
/// ways — <see cref="JdbcGenericDriver"/> (IKVM + pgJDBC) and Npgsql, the engine's own known-good driver, which
/// every other Postgres test in this repo already trusts — and assert the rows come back identical.
/// <see cref="BatchReloadReaderAsync"/> exercises no parameters; <see cref="WatermarkReaderAsync"/>
/// forces the <c>PreparedStatement</c> path across two successive incremental batches.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcReaderParityTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    // Per-instance, not a shared const: JdbcTestDatabase is one IClassFixture instance (one database)
    // for the whole class, but xUnit gives each [Fact] its own JdbcReaderParityTests instance — a fixed
    // table name collided ("relation already exists") the moment a second Fact's InitializeAsync ran
    // against the same already-populated database.
    private readonly string _tableName = $"jdbc_parity_spike_{Guid.NewGuid():N}";

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
        new() { SourceColumn = "created_at", TargetColumn = "created_at" },
    ];

    private static List<CachedColumn> Columns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "varchar(50)", true, false, false),
        new("amount", "numeric(10,2)", true, false, false),
        new("created_at", "timestamp", true, false, false),
    ];

    private NpgsqlConnection _npgsql = null!;
    private JdbcGenericDriver _jdbcDriver = null!;
    private System.Data.Common.DbConnection _jdbc = null!;

    public async Task InitializeAsync()
    {
        _npgsql = db.OpenNpgsqlConnection();
        await ExecuteAsync(_npgsql, $"""
            CREATE TABLE public."{_tableName}" (
                id integer primary key, name varchar(50), amount numeric(10,2), created_at timestamp);
            """);

        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _jdbcDriver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-parity-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", jarPath,
            Readers: [], Staging: [], Writers: []));

        var config = new ConnectionConfig
        {
            Name = "jdbc-parity-test",
            DriverType = "Jdbc",
            ConnectionString = $"{JdbcTestDatabase.JdbcUrl}{db.DatabaseName}",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
        };
        _jdbc = _jdbcDriver.CreateConnection(config, "DbDataSync_Test_Pw1");
        _jdbc.Open();
    }

    public async Task DisposeAsync()
    {
        _jdbc.Dispose();
        await _npgsql.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _tableName };

    /// <summary>PostgresDriver's own published readers — the "known-good" side of every parity test
    /// here, reached through the public IDriver surface rather than PostgresCatalog/PostgresValueBinding
    /// directly (both internal to that project, and rightly so).</summary>
    private static IReadOnlyList<IChangeReader> PostgresReaders { get; } = new PostgresDriver().Readers;

    [Fact]
    public async Task BatchReloadReaderAsync()
    {
        await ExecuteAsync(_npgsql, $"""
            INSERT INTO public."{_tableName}" VALUES
                (1, 'alpha', 12.50, '2026-01-01 10:00:00'),
                (2, 'beta', NULL, '2026-01-02 11:30:00'),
                (3, NULL, 99.99, '2026-01-03 09:15:00');
            """);

        var options = new Dictionary<string, string>();

        var npgsqlReader = PostgresReaders.OfType<BatchReloadReader>().Single();
        var npgsqlResult = await npgsqlReader.ReadChangesAsync(
            _npgsql, Source(), null, ReadIntent.InitialLoad, Mappings, "parity", Columns(), options, CancellationToken.None);
        var expected = await MaterializeAsync(npgsqlResult.Rows);

        var jdbcReader = new BatchReloadReader(JdbcDialect.Instance, new GenericValueBinder(JdbcDialect.Instance, new JdbcProviderFactoryHandle()));
        var jdbcResult = await jdbcReader.ReadChangesAsync(
            _jdbc, Source(), null, ReadIntent.InitialLoad, Mappings, "parity", Columns(), options, CancellationToken.None);
        var actual = await MaterializeAsync(jdbcResult.Rows);

        AssertSameRows(expected, actual);
    }

    [Fact]
    public async Task WatermarkReaderAsync()
    {
        await ExecuteAsync(_npgsql, $"""
            INSERT INTO public."{_tableName}" VALUES
                (1, 'alpha', 12.50, '2026-01-01 10:00:00'),
                (2, 'beta', NULL, '2026-01-02 11:30:00');
            """);

        var options = new Dictionary<string, string> { ["watermarkColumn"] = "created_at" };

        var npgsqlReader = PostgresReaders.OfType<WatermarkReader>().Single();
        var jdbcReader = new WatermarkReader(JdbcDialect.Instance, new GenericValueBinder(JdbcDialect.Instance, new JdbcProviderFactoryHandle()));

        // First batch: no previous watermark, no PreparedStatement parameter yet — establishes a
        // matching starting watermark on both sides.
        var npgsqlFirst = await npgsqlReader.ReadChangesAsync(
            _npgsql, Source(), null, ReadIntent.Changes, Mappings, "parity", Columns(), options, CancellationToken.None);
        var expectedFirst = await MaterializeAsync(npgsqlFirst.Rows);

        var jdbcFirst = await jdbcReader.ReadChangesAsync(
            _jdbc, Source(), null, ReadIntent.Changes, Mappings, "parity", Columns(), options, CancellationToken.None);
        var actualFirst = await MaterializeAsync(jdbcFirst.Rows);

        AssertSameRows(expectedFirst, actualFirst);
        Assert.Equal(npgsqlFirst.NewWatermark, jdbcFirst.NewWatermark);

        // Second batch: a real bound parameter on both sides — this is what forces JdbcCommand's new
        // PreparedStatement/name-to-position translation path.
        await ExecuteAsync(_npgsql, $"""
            INSERT INTO public."{_tableName}" VALUES (3, NULL, 99.99, '2026-01-03 09:15:00');
            """);

        var npgsqlSecond = await npgsqlReader.ReadChangesAsync(
            _npgsql, Source(), npgsqlFirst.NewWatermark, ReadIntent.Changes, Mappings, "parity", Columns(), options, CancellationToken.None);
        var expectedSecond = await MaterializeAsync(npgsqlSecond.Rows);

        var jdbcSecond = await jdbcReader.ReadChangesAsync(
            _jdbc, Source(), jdbcFirst.NewWatermark, ReadIntent.Changes, Mappings, "parity", Columns(), options, CancellationToken.None);
        var actualSecond = await MaterializeAsync(jdbcSecond.Rows);

        AssertSameRows(expectedSecond, actualSecond);
    }

    private static async Task<List<Dictionary<string, string>>> MaterializeAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var results = new List<Dictionary<string, string>>();
        await foreach (var row in rows)
        {
            var byName = new Dictionary<string, string>();
            foreach (var column in row.Schema.ColumnNames)
                byName[column] = Format(row[column]);
            results.Add(byName);
        }
        return results;
    }

    /// <summary>Invariant-culture string form, so a <c>decimal</c> from Npgsql and a <c>decimal</c> from
    /// the JDBC path compare equal even if one carries trailing zeros the other doesn't, and so a
    /// missing/null value from either side reads identically.</summary>
    private static string Format(object? value) => value switch
    {
        null or DBNull => "<NULL>",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<NULL>",
    };

    private static void AssertSameRows(List<Dictionary<string, string>> expected, List<Dictionary<string, string>> actual)
    {
        var expectedSorted = expected.OrderBy(r => r["id"], StringComparer.Ordinal).ToList();
        var actualSorted = actual.OrderBy(r => r["id"], StringComparer.Ordinal).ToList();

        Assert.Equal(expectedSorted.Count, actualSorted.Count);
        for (var i = 0; i < expectedSorted.Count; i++)
            Assert.Equal(expectedSorted[i], actualSorted[i]);
    }
}
