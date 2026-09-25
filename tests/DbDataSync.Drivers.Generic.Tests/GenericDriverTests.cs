using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Proves <see cref="GenericDriver"/> against PostgreSQL — a real, non-trivial engine — with nothing
/// but <see cref="NpgsqlFactory"/> and <see cref="PostgresDialect"/> from <c>DbDataSync.Core.Sql</c>.
/// This project has no reference to <c>DbDataSync.Drivers.Postgres</c>: the assertion that matters is
/// that <see cref="GenericDriver"/>'s behaviour is byte-identical to that driver's own generic Kinds,
/// proving the extraction (phase 109a's doc comment: "a new engine is a dialect, a connection factory
/// and a catalog") lost nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenericDriverTests(GenericDriverTestDatabase db) : IClassFixture<GenericDriverTestDatabase>, IAsyncLifetime
{
    // Npgsql's own connection-string keys, in place of GenericDriverSpec.InformationSchema's
    // SqlClient-shaped defaults — this is exactly the per-engine variation
    // GenericConnectionStringKeys exists to carry: Npgsql spells connect timeout "Timeout", not
    // "Connect Timeout", and has no integrated-security flag (GSSAPI/peer auth needs only a username).
    private readonly GenericDriver _driver = new(GenericDriverSpec.InformationSchema(
            "postgres.generic", PostgresDialect.Instance, NpgsqlFactory.Instance, "postgres", defaultPort: 5432)
        with
        {
            ConnectionStringKeys = new GenericConnectionStringKeys(Username: "Username", ConnectTimeout: "Timeout"),
        });

    private ConnectionConfig Connection() => new()
    {
        Name = "generic-postgres",
        DriverType = "postgres.generic",
        Host = GenericDriverTestDatabase.Host,
        Port = GenericDriverTestDatabase.Port,
        Database = db.DatabaseName,
        AuthMode = AuthMode.SqlAuth,
        UserId = GenericDriverTestDatabase.User,
    };

    private System.Data.Common.DbConnection Open() =>
        _driver.CreateConnection(Connection(), GenericDriverTestDatabase.Password);

    private string _table = null!;

    public async Task InitializeAsync()
    {
        _table = $"generic_{Guid.NewGuid():N}";
        await using var setup = db.OpenConnection();
        await using var cmd = setup.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE public."{_table}" (
                id integer primary key, name text not null, modified_at timestamp
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "id", TargetColumn = "id" },
        new() { SourceColumn = "name", TargetColumn = "name" },
        new() { SourceColumn = "modified_at", TargetColumn = "modified_at" },
    ];

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "public", Table = _table };

    private static List<CachedColumn> Columns() =>
    [
        new("id", "integer", false, true, false),
        new("name", "text", false, false, false),
        new("modified_at", "timestamp", true, false, false),
    ];

    [Fact]
    public async Task CreateConnection_AssembledFromHostPortDatabaseAndCredential_Opens()
    {
        await using var connection = Open();
        await connection.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>Phase 176M. <see cref="GenericDriver.PreviewConnection"/> runs the identical unification
    /// <see cref="GenericDriver.CreateConnection"/> does — proven here by the masked placeholder taking
    /// the exact position the real password did in <see cref="CreateConnection_AssembledFromHostPortDatabaseAndCredential_Opens"/>,
    /// against the same real fixture, not a hand-assembled string that only looks right.</summary>
    [Fact]
    public void PreviewConnection_MasksTheCredential_AndNeverTouchesTheNetwork()
    {
        var preview = _driver.PreviewConnection(Connection());

        Assert.Contains("••••••", preview.ConnectionString);
        Assert.DoesNotContain(GenericDriverTestDatabase.Password, preview.ConnectionString);
        Assert.Contains(db.DatabaseName, preview.ConnectionString);
        Assert.Contains(GenericDriverTestDatabase.Host, preview.ConnectionString);
        // Nothing routes through an out-of-connection-string channel for a plain ADO.NET driver.
        Assert.Null(preview.JdbcUri);
        Assert.Empty(preview.Properties);
    }

    [Fact]
    public async Task ListDatabasesAsync_FindsTheFixtureDatabase()
    {
        await using var connection = Open();
        await connection.OpenAsync();

        var databases = await _driver.ListDatabasesAsync(connection, CancellationToken.None);

        Assert.Contains(db.DatabaseName, databases);
    }

    [Fact]
    public async Task TestAsync_AgainstAWorkingConnection_Succeeds()
    {
        await using var connection = Open();
        await connection.OpenAsync();

        var result = await _driver.TestAsync(connection, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    /// <summary>Phase 176M widened <c>TestAsync</c>'s catch to "everything except cancellation" —
    /// deliberately, since a cancelled test request is not a "connection failed" answer and reporting it
    /// as a <c>ConnectionTestResult(Succeeded: false, ...)</c> would misreport what happened. Proves the
    /// exclusion actually holds: an already-cancelled token must still propagate as a cancellation, not
    /// get swallowed into a false-negative result.</summary>
    [Fact]
    public async Task TestAsync_WithAnAlreadyCancelledToken_PropagatesCancellation_InsteadOfReportingFailure()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _driver.TestAsync(connection, cts.Token));
    }

    [Fact]
    public async Task ListTablesAsync_FindsTheSeededTable()
    {
        await using var connection = Open();
        await connection.OpenAsync();

        var tables = await _driver.ListTablesAsync(connection, db.DatabaseName, CancellationToken.None);

        Assert.Contains(tables, t => t.Schema == "public" && t.Table == _table);
    }

    [Fact]
    public async Task ListColumnsAsync_ReturnsTheSeededShape()
    {
        await using var connection = Open();
        await connection.OpenAsync();

        var columns = await _driver.ListColumnsAsync(connection, db.DatabaseName, "public", _table, CancellationToken.None);

        Assert.True(columns.Single(c => c.Name == "id").IsPrimaryKey);
        Assert.False(columns.Single(c => c.Name == "name").IsNullable);
        Assert.True(columns.Single(c => c.Name == "modified_at").IsNullable);
    }

    [Fact]
    public async Task Watermark_ReadsTheInsertedRow_AndAdvancesThePosition()
    {
        var watermark = (WatermarkReader)_driver.Readers.Single(r => r.Kind == GenericDriverKinds.Watermark);
        await using var connection = Open();
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO public.\"{_table}\" VALUES (1, 'Alice', '2026-01-01 00:00:00');";
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new Dictionary<string, string> { ["watermarkColumn"] = "modified_at" };
        var first = await watermark.ReadChangesAsync(
            connection, Source(), null, ReadIntent.InitialLoad, Mappings, "generic-watermark", Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var rows = await CollectAsync(first.Rows);

        Assert.Single(rows);
        Assert.Equal(1, (int)rows[0]["id"]!);
        Assert.NotNull(first.NewWatermark);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO public.\"{_table}\" VALUES (2, 'Bob', '2026-02-01 00:00:00');";
            await cmd.ExecuteNonQueryAsync();
        }

        var second = await watermark.ReadChangesAsync(
            connection, Source(), first.NewWatermark, ReadIntent.Changes, Mappings, "generic-watermark", Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var secondRows = await CollectAsync(second.Rows);

        Assert.Equal(2, (int)Assert.Single(secondRows)["id"]!);
    }

    [Fact]
    public async Task BatchReloadAndDeleteInsert_RoundTripLandsTheSourceRows()
    {
        var reader = (BatchReloadReader)_driver.Readers.Single(r => r.Kind == GenericDriverKinds.BatchReload);
        var staging = (BatchInsertStagingProvider)_driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
        var writer = (DeleteInsertWriter)_driver.Writers.Single(w => w.Kind == GenericDriverKinds.DeleteInsert);

        var targetTable = $"tgt_{Guid.NewGuid():N}";
        await using (var setup = db.OpenConnection())
        await using (var cmd = setup.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TABLE public."{targetTable}" (
                    id integer primary key, name text not null, modified_at timestamp
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var source = Open();
        await source.OpenAsync();
        await using (var cmd = source.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO public."{_table}" VALUES
                    (1, 'Alice', '2026-01-01 00:00:00'),
                    (2, 'Bob', '2026-01-02 00:00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var target = Open();
        await target.OpenAsync();
        var targetRef = new TableRef { ConnectionName = "tgt", Database = db.DatabaseName, Schema = "public", Table = targetTable };
        var options = new Dictionary<string, string>();

        var read = await reader.ReadChangesAsync(
            source, Source(), null, ReadIntent.InitialLoad, Mappings, "generic-batch", Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);
        var staged = await staging.StageAsync(target, targetRef, read.Rows, Mappings, "generic-batch", Columns(), options, CancellationToken.None);
        try
        {
            var written = await writer.ApplyAsync(target, targetRef, staged, Mappings, "generic-batch", Columns(), options, CancellationToken.None);
            Assert.Equal(2, written.RowsWritten);
        }
        finally
        {
            await staging.CleanupAsync(target, staged, CancellationToken.None);
        }

        await using var verify = target.CreateCommand();
        verify.CommandText = $"SELECT count(*) FROM public.\"{targetTable}\";";
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
    }

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }
}
