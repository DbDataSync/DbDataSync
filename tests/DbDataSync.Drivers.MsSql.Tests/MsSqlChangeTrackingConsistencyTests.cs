using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// Guards the read-consistency behaviour investigated in
/// architecture/planning/done/task-run-errors-during-high-volume-workload.md: CHANGETABLE and the
/// source table are read at different instants under READ COMMITTED, so a concurrent delete can leave
/// a reported insert/update with no row behind it.
/// <para>
/// These are load-shaped rather than deterministic — the race either fires during the read or it does
/// not, and a run where it does not fire passes vacuously. The row count is set where it is because at
/// that size it fired on every attempt while the cause was being established (28,000 anomalous rows
/// out of 312,000). Kept honest rather than made to look exact.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlChangeTrackingConsistencyTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private const int SeededRows = 150_000;

    private readonly MsSqlChangeTrackingReader _reader = new();
    private SqlConnection _connection = null!;
    private string _tableName = null!;
    private string _baselineWatermark = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"CtRace_{Guid.NewGuid():N}";

        await ExecuteAsync(_connection, $"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Region NVARCHAR(20) NOT NULL,
                Amount DECIMAL(18,2) NOT NULL
            );
            """);
        await ExecuteAsync(_connection, $"ALTER TABLE dbo.[{_tableName}] ENABLE CHANGE_TRACKING;");

        // Captured after enabling tracking but before seeding, so every seeded row is an outstanding
        // change. A literal "0" only happens to be valid for the first tracked table in a fresh
        // database — once the database's version has moved on it falls below the table's minimum
        // valid version and the reader rejects it, correctly.
        _baselineWatermark = await ScalarAsync("SELECT CHANGE_TRACKING_CURRENT_VERSION();");

        await ExecuteAsync(_connection, $"""
            INSERT INTO dbo.[{_tableName}] (Id, Region, Amount)
            SELECT TOP ({SeededRows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'EU', 1.00
            FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
            """);
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = _tableName,
    };

    /// <summary>
    /// Deletes in batches for as long as the reader is streaming, on its own connection.
    /// <para>
    /// <c>SET DEADLOCK_PRIORITY LOW</c> makes this session always the one SQL Server's deadlock monitor
    /// kills when this loop and the reader's own scan genuinely deadlock against each other — a real,
    /// structural resolution of which side is expendable, not a retry or a sleep. This loop is disposable
    /// background load with nowhere else to report a failure; the reader is the thing under test, and the
    /// catch below already treats any <see cref="SqlException"/> here (deadlock victim or otherwise) as
    /// "done, nothing to report" — so being the side that's always chosen just turns an occasional
    /// test-crashing race (the reader could be picked instead, with no equivalent catch around it) into
    /// the outcome this method was already written to handle silently every time.
    /// </para>
    /// </summary>
    private async Task DeleteConcurrentlyAsync(CancellationToken cancellationToken)
    {
        await using var deleter = db.OpenConnection();
        await ExecuteAsync(deleter, "SET DEADLOCK_PRIORITY LOW;");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteAsync(deleter, $"DELETE TOP (2000) FROM dbo.[{_tableName}] WHERE Id > 50000;");
            }
            catch (SqlException)
            {
                return; // the table ran dry, this session was the deadlock victim, or the test finished —
                        // nothing here needs reporting; see this method's own doc comment.
            }
        }
    }

    private async Task<(List<ChangeRow> Rows, ReadDiagnostics Diagnostics)> ReadUnderDeletePressureAsync(
        IReadOnlyDictionary<string, string> options)
    {
        // Reading from the pre-seed baseline makes every seeded row an outstanding insert, so the
        // reader has a large stream to work through while the deleter runs against it.
        var read = await _reader.ReadChangesAsync(_connection, Source(), _baselineWatermark, ReadIntent.Changes, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var deleting = DeleteConcurrentlyAsync(stop.Token);

        var rows = new List<ChangeRow>();
        try
        {
            await foreach (var row in read.Rows)
                rows.Add(row);
        }
        finally
        {
            await stop.CancelAsync();
            await deleting;
        }

        return (rows, read.Diagnostics!);
    }

    /// <summary>
    /// The regression guard. Before the fix these rows arrived with every column NULL — including the
    /// primary key, because <c>base.*</c> re-emitted it and overwrote CHANGETABLE's value — and the
    /// writer then tried to insert a NULL key into a NOT NULL column.
    /// </summary>
    [Fact]
    public async Task ConcurrentDeletes_NeverYieldARowWithoutItsKey()
    {
        var (rows, diagnostics) = await ReadUnderDeletePressureAsync(new Dictionary<string, string>());

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.True(row.Schema.TryGetOrdinal("Id", out var idOrdinal), "every row must know its key column");
            Assert.NotNull(row[idOrdinal]);
        });

        // Informational: if this is 0 the race did not fire this run and the assertions above passed
        // vacuously. It is not asserted on, because forcing the race is not something a test can do.
        Console.WriteLine($"rows yielded: {rows.Count}, skipped as source-row-gone: {diagnostics.RowsSkippedSourceRowGone}");
    }

    /// <summary>Rows that are skipped must be skipped for the right reason — a non-delete whose source
    /// row vanished — and never a delete, whose missing row is the entire point of it.</summary>
    [Fact]
    public async Task ConcurrentDeletes_StillDeliverDeletesWithTheirKeys()
    {
        var (rows, _) = await ReadUnderDeletePressureAsync(new Dictionary<string, string>());

        foreach (var deletion in rows.Where(r => r.Operation == ChangeOperation.Delete))
            Assert.NotNull(deletion["Id"]);
    }

    /// <summary>
    /// The whole point of the option: with snapshot isolation the two reads are consistent, so there
    /// is nothing to skip. A non-zero count here would mean the transaction is not actually covering
    /// the query.
    /// </summary>
    [Fact]
    public async Task SnapshotIsolation_LeavesNothingToSkip()
    {
        await ExecuteAsync(_connection, $"ALTER DATABASE [{db.DatabaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON;");
        try
        {
            var options = new Dictionary<string, string> { [MsSqlChangeTrackingReader.SnapshotIsolationOption] = "true" };
            var (rows, diagnostics) = await ReadUnderDeletePressureAsync(options);

            Assert.NotEmpty(rows);
            Assert.Equal(0, diagnostics.RowsSkippedSourceRowGone);
            Assert.All(rows, row => Assert.NotNull(row["Id"]));
        }
        finally
        {
            await ExecuteAsync(_connection, $"ALTER DATABASE [{db.DatabaseName}] SET ALLOW_SNAPSHOT_ISOLATION OFF;");
        }
    }

    /// <summary>Asking for snapshot isolation on a database that doesn't allow it must say what to
    /// run, not surface SQL Server's error 3952 verbatim.</summary>
    [Fact]
    public async Task SnapshotIsolation_WithoutTheDatabaseSetting_ExplainsWhatToEnable()
    {
        var options = new Dictionary<string, string> { [MsSqlChangeTrackingReader.SnapshotIsolationOption] = "true" };
        var read = await _reader.ReadChangesAsync(_connection, Source(), _baselineWatermark, ReadIntent.Changes, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in read.Rows) { }
        });

        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION ON", ex.Message);
        Assert.Contains(db.DatabaseName, ex.Message);
    }

    /// <summary>Sanity: with no concurrent writer the reader returns every seeded row, so the skip
    /// path cannot be quietly discarding work under normal conditions.</summary>
    [Fact]
    public async Task WithoutConcurrentWrites_EveryRowIsRead()
    {
        var stopwatch = Stopwatch.StartNew();
        var read = await _reader.ReadChangesAsync(
            _connection, Source(), _baselineWatermark, ReadIntent.Changes, [], "mapping", [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(), CancellationToken.None);

        var count = 0;
        await foreach (var row in read.Rows)
        {
            Assert.NotNull(row["Id"]);
            count++;
        }

        Assert.Equal(SeededRows, count);
        Assert.Equal(0, read.Diagnostics!.RowsSkippedSourceRowGone);
        Console.WriteLine($"read {count} rows in {stopwatch.ElapsedMilliseconds} ms");
    }
}
