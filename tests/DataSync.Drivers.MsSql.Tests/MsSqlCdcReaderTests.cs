using System.Diagnostics;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// CDC against a real SQL Server with Agent running, because that is the only place it exists: the
/// capture is an Agent job, and without one <c>sp_cdc_enable_table</c> succeeds and then nothing is
/// ever captured. The container ships Agent stopped, so <c>docker-compose.yml</c> turns it on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlCdcReaderTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlCdcReader _reader = new();
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"CdcProbe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);

        if (!await MsSqlCdcCatalog.CdcIsEnabledAsync(_connection, CancellationToken.None))
            await ExecuteAsync("EXEC sys.sp_cdc_enable_db;");

        // Retried on 1205. Enabling capture registers the Agent jobs, which touches msdb — and so
        // does the capture job that is already running for this database. SQL Server deadlocks the two
        // occasionally and says "Rerun the transaction", which is what this does. Only in the fixture:
        // in the product the same failure surfaces to the operator with the server's own message,
        // which is honest, and retrying a DDL apply on their behalf is a separate decision.
        await EnableCaptureWithRetryAsync();

        // sp_cdc_enable_db creates the Agent jobs; it does not wait for them to start scanning. Until
        // the first scan there is no max LSN, and the reader correctly reports that as "the capture
        // job has not run" — which is the right answer and not the one these tests are asking about.
        await WaitForCaptureStartedAsync();
    }

    private async Task EnableCaptureWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync($"""
                    EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{_tableName}',
                         @role_name = NULL, @supports_net_changes = 1;
                    """);
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));

                // The failed attempt can still have registered the capture instance before deadlocking
                // on the job registration, and re-running then fails with "already enabled" instead.
                if (await MsSqlCdcCatalog.FindCaptureInstanceAsync(
                        _connection, "dbo", _tableName, CancellationToken.None) is not null)
                    return;
            }
        }
    }

    /// <summary>
    /// <c>sp_cdc_enable_table</c> catches the deadlock and re-raises its own error with the original
    /// quoted in the text, so the 1205 is in the message rather than in <c>Errors</c>. Matching the
    /// number alone silently never retried — which is how this looked fixed for one run.
    /// </summary>
    private static bool IsDeadlock(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number == 1205)
        || ex.Message.Contains("was deadlocked on lock resources", StringComparison.Ordinal);

    /// <summary>
    /// Waits until capture is actually established: a max LSN means the job has scanned, and a min
    /// LSN means the instance's start has been recorded. Both, because the window between them is
    /// exactly where the reader's two boundary bugs lived.
    /// </summary>
    private async Task WaitForCaptureStartedAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(90))
        {
            var max = await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None);
            var min = await MsSqlCdcCatalog.GetMinLsnAsync(_connection, $"dbo_{_tableName}", CancellationToken.None);
            if (max is not null && min is not null)
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException(
            "The CDC capture job never produced a maximum LSN. Is SQL Server Agent running? " +
            "(docker-compose.yml sets MSSQL_AGENT_ENABLED on mssql-source.)");
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test",
        Database = db.DatabaseName,
        Schema = "dbo",
        Table = _tableName,
    };

    private Task<ReadResult> ReadAsync(string? watermark, SourceTableRef? source = null) =>
        _reader.ReadChangesAsync(
            _connection, source ?? Source(), watermark, [], new Dictionary<string, string>(), CancellationToken.None);

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    /// <summary>
    /// Drains everything CDC has captured so far and returns the position after it.
    /// <para>
    /// A first pass reads the table whole, and CDC has *also* captured the rows that were inserted
    /// after capture was enabled — so the pass after a full load re-delivers them. That is harmless
    /// (writers upsert) and unavoidable, and it is not what these tests are about, so they settle
    /// first and then make the change under test.
    /// </para>
    /// </summary>
    private async Task<string> SettleAsync(string watermark, SourceTableRef? source = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            // Waiting first is the part that matters. A read taken before the capture job has scanned
            // past this position returns nothing — not because there is nothing, but because nothing
            // has been captured *yet* — and treating that as settled is what left the rows to turn up
            // in the middle of the assertion.
            watermark = await WaitForCaptureAsync(watermark);

            var result = await ReadAsync(watermark, source);
            var rows = await CollectAsync(result.Rows);
            watermark = result.NewWatermark;

            if (rows.Count == 0)
                return watermark;
        }

        throw new TimeoutException("CDC kept returning changes with nothing writing to the table.");
    }

    /// <summary>
    /// The capture job scans the log on its own schedule, so a change is not readable the instant it
    /// is committed. Waiting for the max LSN to move past a known point is what an operator's next
    /// pass does too — this is the real latency, not a test artifact.
    /// </summary>
    private async Task<string> WaitForCaptureAsync(string after)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(60))
        {
            var max = await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None);
            if (max is not null && MsSqlCdcCatalog.Compare(max, MsSqlCdcCatalog.FromWatermark(after)) > 0)
                return MsSqlCdcCatalog.ToWatermark(max);

            await Task.Delay(500);
        }

        throw new TimeoutException(
            "The CDC capture job did not advance within 60s. Is SQL Server Agent running? " +
            "(docker-compose.yml sets MSSQL_AGENT_ENABLED.)");
    }

    /// <summary>CDC's change table holds only what happened since capture was enabled, so a table that
    /// already had rows would otherwise start half-replicated with nothing to say so.</summary>
    [Fact]
    public async Task WithNoStoredPosition_EveryRowIsReadAsAnInsert()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var result = await ReadAsync(null);
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
        Assert.False(string.IsNullOrEmpty(result.NewWatermark));
    }

    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (3, 'Carol');");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync($"DELETE FROM dbo.[{_tableName}] WHERE Id = 1;");
        await WaitForCaptureAsync(start);

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        Assert.Equal(ChangeOperation.Insert, Assert.Single(rows, r => (int)r["Id"]! == 3).Operation);
        Assert.Equal(ChangeOperation.Update, Assert.Single(rows, r => (int)r["Id"]! == 2).Operation);

        // A delete carries the row's values as of the delete — unlike Change Tracking, where the
        // LEFT JOIN yields NULL for every non-key column and only the key is reliable.
        var deleted = Assert.Single(rows, r => (int)r["Id"]! == 1);
        Assert.Equal(ChangeOperation.Delete, deleted.Operation);
        Assert.Equal("Alice", deleted["Name"]);
    }

    /// <summary>
    /// The off-by-one `sys.fn_cdc_increment_lsn` exists to prevent. CDC's window is inclusive of
    /// `@from`, so without it every pass re-reads the previous pass's final change — harmless to the
    /// data, and enough to make a monitoring graph say a quiet source is busy.
    /// </summary>
    [Fact]
    public async Task ReadingTwiceWithNoChangeInBetween_ReturnsNothingTheSecondTime()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'Bob');");
        var afterInsert = await WaitForCaptureAsync(start);

        var first = await ReadAsync(start);
        Assert.Single(await CollectAsync(first.Rows));

        var second = await ReadAsync(first.NewWatermark);
        Assert.Empty(await CollectAsync(second.Rows));
        Assert.Equal(first.NewWatermark, second.NewWatermark);
        Assert.False(string.IsNullOrEmpty(afterInsert));
    }

    /// <summary>
    /// Net changes is what makes CDC usable for mirroring: a row updated fifty times between passes is
    /// one row to write, not fifty. Three updates to one key inside one window is the smallest case
    /// that tells the two apart.
    /// </summary>
    [Fact]
    public async Task ThreeUpdatesToOneKey_CollapseToOneRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'a');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'b' WHERE Id = 1;");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'c' WHERE Id = 1;");
        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Name = 'd' WHERE Id = 1;");
        await WaitForCaptureAsync(start);

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        var row = Assert.Single(rows);
        Assert.Equal("d", row["Name"]);
    }

    /// <summary>
    /// A position older than the capture instance retains cannot be served, and the fix is a reload
    /// rather than anything the reader can do. Shared with Change Tracking, which is the point of the
    /// exception existing at all.
    /// </summary>
    [Fact]
    public async Task APositionBelowTheOldestAvailable_IsReportedAsExpired()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        await ReadAsync(null);

        // All-zeroes sorts below every real LSN, which is exactly what a position discarded by
        // cleanup looks like from here.
        var problem = await Assert.ThrowsAsync<PositionExpiredException>(
            () => ReadAsync(MsSqlCdcCatalog.ToWatermark(new byte[10])));

        Assert.Equal("Change Data Capture", problem.Mechanism);
        Assert.Contains("has to be reloaded", problem.Message);
    }

    /// <summary>A table nobody captured is a configuration answer, not a crash.</summary>
    [Fact]
    public async Task ATableWithNoCaptureInstance_SaysSo()
    {
        var uncaptured = $"NotCaptured_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{uncaptured}] (Id INT NOT NULL PRIMARY KEY);");

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = uncaptured,
        };

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => _reader.ReadChangesAsync(
            _connection, source, null, [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("Change Data Capture is not enabled", problem.Message);
    }

    /// <summary>
    /// A capture instance covers the columns it was created with, so a column added since is not in
    /// it. Selecting one fails with an invalid-column error naming the change table, which is a long
    /// way from the actual problem — so the reader says what the problem is instead.
    /// </summary>
    [Fact]
    public async Task AColumnAddedAfterCapture_IsReportedAgainstTheMappingRatherThanTheChangeTable()
    {
        await ExecuteAsync($"ALTER TABLE dbo.[{_tableName}] ADD Region NVARCHAR(20) NULL;");
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name, Region) VALUES (1, 'Alice', 'north');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"UPDATE dbo.[{_tableName}] SET Region = 'south' WHERE Id = 1;");
        await WaitForCaptureAsync(start);

        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Id", TargetColumn = "Id" },
            new() { SourceColumn = "Region", TargetColumn = "Region" },
        };

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var result = await _reader.ReadChangesAsync(
                _connection, Source(), start, mappings, new Dictionary<string, string>(), CancellationToken.None);
            await CollectAsync(result.Rows);
        });

        Assert.Contains("does not capture 'Region'", problem.Message);
    }

    /// <summary>
    /// The fallback. A capture instance created without <c>@supports_net_changes</c> has no
    /// net-changes function, so the reader reads every intermediate change instead — which is correct
    /// and more work per pass, and is why it reports which shape it used rather than being silent.
    /// </summary>
    [Fact]
    public async Task AnInstanceWithoutNetChanges_ReadsEveryIntermediateChange()
    {
        var table = $"CdcAll_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync($"""
            EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{table}',
                 @role_name = NULL, @supports_net_changes = 0;
            """);

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = table,
        };
        var instance = await MsSqlCdcCatalog.FindCaptureInstanceAsync(
            _connection, "dbo", table, CancellationToken.None);
        Assert.False(instance!.SupportsNetChanges);

        await ExecuteAsync($"INSERT INTO dbo.[{table}] (Id, Name) VALUES (1, 'a');");
        var start = await SettleAsync((await ReadAsync(null, source)).NewWatermark, source);

        await ExecuteAsync($"UPDATE dbo.[{table}] SET Name = 'b' WHERE Id = 1;");
        await ExecuteAsync($"UPDATE dbo.[{table}] SET Name = 'c' WHERE Id = 1;");
        await WaitForCaptureAsync(start);

        var rows = await CollectAsync((await ReadAsync(start, source)).Rows);

        // Two, where net changes would have collapsed them to one — the difference the reader's
        // preference exists to buy.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Update, r.Operation));
        Assert.Equal(["b", "c"], rows.Select(r => (string)r["Name"]!));
    }
}
