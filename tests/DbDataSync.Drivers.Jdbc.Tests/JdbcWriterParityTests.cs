using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.Postgres;
using Npgsql;

namespace DbDataSync.Drivers.Jdbc.Tests;

/// <summary>
/// Phase 172V — the write-side counterpart to <see cref="JdbcReaderParityTests"/>. Reads once from a real
/// Postgres source table (Npgsql, the same known-good side every parity test here uses), then stages and
/// writes the same change set into two target tables — one through <see cref="PostgresDialect"/>'s own
/// native path, one through <see cref="JdbcGenericDriver"/>'s <c>java.sql.Connection</c> — and asserts the
/// two targets end up identical. Every writer component (<see cref="BatchInsertStagingProvider"/>,
/// <see cref="DeleteInsertWriter"/>) is <c>DbDataSync.Drivers.Generic</c>'s own, unchanged — this phase's
/// entire scope was making <see cref="Ado.JdbcConnection.BeginDbTransaction"/> real, so what these
/// tests actually pin is that a real <c>java.sql.Connection</c> transaction round-trips through it
/// correctly, not any new SQL-generation logic.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JdbcWriterParityTests(JdbcTestDatabase db) : IClassFixture<JdbcTestDatabase>, IAsyncLifetime
{
    private readonly string _sourceTable = $"jdbc_writer_src_{Guid.NewGuid():N}";
    private readonly string _nativeTargetTable = $"jdbc_writer_tgt_native_{Guid.NewGuid():N}";
    private readonly string _jdbcTargetTable = $"jdbc_writer_tgt_jdbc_{Guid.NewGuid():N}";

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "amount", TargetColumn = "amount" },
    ];

    // Three separate connections, not one reused — Npgsql (no MARS) refuses a second command while an
    // earlier one's DbDataReader is still streaming, and read.Rows here is exactly that: a reader kept
    // open by BatchReloadReader while staging concurrently issues INSERTs against it. PostgresPipelineTests
    // hits the same constraint with its own _source/_target split; this needs a third for the second target.
    private NpgsqlConnection _source = null!;
    private NpgsqlConnection _nativeTarget = null!;
    private JdbcGenericDriver _jdbcDriver = null!;
    private System.Data.Common.DbConnection _jdbc = null!;

    // PostgresCatalog/PostgresValueBinding are internal to DbDataSync.Drivers.Postgres — reached through
    // the public IDriver surface instead, the same way JdbcReaderParityTests' own PostgresReaders does.
    private static readonly PostgresDriver NativePostgresDriver = new();

    private readonly BatchReloadReader _reader =
        (BatchReloadReader)NativePostgresDriver.Readers.Single(r => r.Kind == GenericDriverKinds.BatchReload);

    private readonly BatchInsertStagingProvider _nativeStaging =
        (BatchInsertStagingProvider)NativePostgresDriver.StagingProviders.Single(s => s.Kind == GenericDriverKinds.StagingTable);
    private readonly DeleteInsertWriter _nativeWriter =
        (DeleteInsertWriter)NativePostgresDriver.Writers.Single(w => w.Kind == GenericDriverKinds.DeleteInsert);

    private readonly BatchInsertStagingProvider _jdbcStaging = new(JdbcDialect.Instance, JdbcCatalog.Instance);
    private readonly DeleteInsertWriter _jdbcWriter =
        new(JdbcDialect.Instance, JdbcCatalog.Instance,
            new GenericValueBinder(JdbcDialect.Instance, new JdbcProviderFactoryHandle()));

    public async Task InitializeAsync()
    {
        _source = db.OpenNpgsqlConnection();
        _nativeTarget = db.OpenNpgsqlConnection();
        await ExecuteAsync(_source, $"""
            CREATE TABLE public."{_sourceTable}" (id integer primary key, name varchar(50), amount numeric(10,2));
            """);
        await ExecuteAsync(_source, $"""
            CREATE TABLE public."{_nativeTargetTable}" (id integer primary key, name varchar(50) not null, amount numeric(10,2));
            """);
        await ExecuteAsync(_source, $"""
            CREATE TABLE public."{_jdbcTargetTable}" (id integer primary key, name varchar(50) not null, amount numeric(10,2));
            """);

        var jarPath = Path.Combine(AppContext.BaseDirectory, "postgresql.jar");
        _jdbcDriver = new JdbcGenericDriver(new JdbcDriverSpec(
            "jdbc-writer-test", JdbcDialect.Instance, JdbcCatalog.Instance, "org.postgresql.Driver", [jarPath],
            Readers: [], Staging: [], Writers: [GenericDriverKinds.DeleteInsert]));

        var config = new ConnectionConfig
        {
            Name = "jdbc-writer-test",
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
        await _source.DisposeAsync();
        await _nativeTarget.DisposeAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _sourceTable };

    private TableRef NativeTarget() =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = _nativeTargetTable };

    private TableRef JdbcTarget() =>
        new() { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = _jdbcTargetTable };

    private static List<CachedColumn> Columns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "varchar(50)", false, false, false),
        new("amount", "numeric(10,2)", true, false, false),
    ];

    private async Task<long> ReloadNativeAsync(IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        var read = await _reader.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, "writer-parity", Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var staged = await _nativeStaging.StageAsync(
            _nativeTarget, NativeTarget(), read.Rows, Mappings, "writer-parity", Columns(), options, CancellationToken.None);
        try
        {
            return (await _nativeWriter.ApplyAsync(
                _nativeTarget, NativeTarget(), staged, Mappings, "writer-parity", Columns(), options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await _nativeStaging.CleanupAsync(_nativeTarget, staged, CancellationToken.None);
        }
    }

    private async Task<long> ReloadJdbcAsync(IReadOnlyDictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        var read = await _reader.ReadChangesAsync(
            _source, Source(), null, ReadIntent.InitialLoad, Mappings, "writer-parity", Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var staged = await _jdbcStaging.StageAsync(
            _jdbc, JdbcTarget(), read.Rows, Mappings, "writer-parity", Columns(), options, CancellationToken.None);
        try
        {
            return (await _jdbcWriter.ApplyAsync(
                _jdbc, JdbcTarget(), staged, Mappings, "writer-parity", Columns(), options, CancellationToken.None)).RowsWritten;
        }
        finally
        {
            await _jdbcStaging.CleanupAsync(_jdbc, staged, CancellationToken.None);
        }
    }

    private async Task<Dictionary<int, (string Name, decimal? Amount)>> TargetRowsAsync(string table)
    {
        await using var cmd = _source.CreateCommand();
        cmd.CommandText = $"SELECT id, name, amount FROM public.\"{table}\";";
        var rows = new Dictionary<int, (string, decimal?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows[reader.GetInt32(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2));
        return rows;
    }

    [Fact]
    public async Task FullReload_ProducesTheSameRowsAsTheNativeDriver()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO public."{_sourceTable}" VALUES
                (1, 'alice', 12.50),
                (2, 'bob', NULL),
                (3, 'carol', 99.99);
            """);

        var nativeWritten = await ReloadNativeAsync();
        var jdbcWritten = await ReloadJdbcAsync();

        Assert.Equal(3, nativeWritten);
        Assert.Equal(nativeWritten, jdbcWritten);
        Assert.Equal(await TargetRowsAsync(_nativeTargetTable), await TargetRowsAsync(_jdbcTargetTable));
    }

    [Fact]
    public async Task Reload_RemovesRowsDeletedAtTheSource_SameAsTheNativeDriver()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO public."{_sourceTable}" VALUES (1, 'alice', 1), (2, 'bob', 2);
            """);
        await ReloadNativeAsync();
        await ReloadJdbcAsync();

        await ExecuteAsync(_source, $"""DELETE FROM public."{_sourceTable}" WHERE id = 2;""");
        await ReloadNativeAsync();
        await ReloadJdbcAsync();

        var native = await TargetRowsAsync(_nativeTargetTable);
        var jdbc = await TargetRowsAsync(_jdbcTargetTable);
        Assert.Single(native);
        Assert.Equal(native, jdbc);
    }

    /// <summary>The regression this phase's transaction primitive actually has to prevent: a batch that
    /// fails partway through must not leave the delete's half of the pair applied. Forced by a source row
    /// with a NULL name against a target column staging deliberately leaves nullable regardless of the
    /// target's own constraint (<c>BatchInsertStagingProvider</c>'s own doc comment) — the violation
    /// surfaces at INSERT time, inside the transaction <see cref="DeleteInsertWriter"/> opened, which then
    /// explicitly rolls back and rethrows.</summary>
    [Fact]
    public async Task AFailedWrite_RollsBackRatherThanLeavingTheScopeEmptied()
    {
        await ExecuteAsync(_source, $"""
            INSERT INTO public."{_sourceTable}" VALUES (1, 'alice', 1);
            """);
        await ReloadJdbcAsync();
        Assert.Single(await TargetRowsAsync(_jdbcTargetTable));

        // name is NOT NULL on the target but nullable on the source — this row stages fine (staging
        // itself has no NOT NULL constraints) and fails only once DeleteInsertWriter tries to INSERT it.
        await ExecuteAsync(_source, $"""
            UPDATE public."{_sourceTable}" SET name = NULL WHERE id = 1;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => ReloadJdbcAsync());

        // The delete half of the pair ran inside the same transaction as the failed insert — if
        // JdbcTransaction's Commit/Rollback didn't round-trip to a real java.sql.Connection correctly,
        // this would come back empty (delete committed, insert not) instead of unchanged.
        var rows = await TargetRowsAsync(_jdbcTargetTable);
        Assert.Single(rows);
        Assert.Equal("alice", rows[1].Name);
    }
}
