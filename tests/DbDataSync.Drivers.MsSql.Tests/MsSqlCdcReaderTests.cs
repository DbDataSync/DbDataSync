using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

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
        _connection = db.OpenConnection(pooled: false);
        _tableName = $"CdcProbe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);

        await CdcCaptureJob.EnableDbAsync(_connection);

        // Retried on 1205. Enabling capture registers the Agent jobs, which touches msdb — and so
        // does the capture job that is already running for this database. SQL Server deadlocks the two
        // occasionally and says "Rerun the transaction", which is what this does. Only in the fixture:
        // in the product the same failure surfaces to the operator with the server's own message,
        // which is honest, and retrying a DDL apply on their behalf is a separate decision.
        await EnableCaptureWithRetryAsync();

        // From here the fixture drives the scan by hand (ScanAsync stops the Agent capture job first),
        // so the tests that wait on CDC are waiting on nothing but their own scan — "did not advance
        // within 60s" and cleanup rounding against an un-scanned change table both stop being possible.
        // One scan now so the instance has a max and min LSN; the reader reports their absence as "the
        // capture job has not run", which is right and not what these tests are asking about.
        await CdcCaptureJob.ScanAsync(_connection);
        await AssertCaptureEstablishedAsync();
    }

    /// <summary>The scan in <see cref="InitializeAsync"/> establishes both boundaries; this fails
    /// fast, with the capture job's state, if it somehow did not.</summary>
    private async Task AssertCaptureEstablishedAsync()
    {
        var max = await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None);
        var min = await MsSqlCdcCatalog.GetMinLsnAsync(_connection, $"dbo_{_tableName}", CancellationToken.None);
        if (max is null || min is null)
            throw new InvalidOperationException(
                $"CDC capture is not established after a scan (max={max is not null}, min={min is not null}). " +
                $"{await CdcCaptureJob.DiagnoseAsync(_connection)}.");
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

    public async Task DisposeAsync()
    {
        // Stop this database's capture job before the fixture drops the database, so it is not left
        // scanning a vanished database and contending for the log reader with the next CDC test class.
        try { await CdcCaptureJob.StopCaptureJobAsync(_connection); }
        catch { /* best-effort teardown */ }
        _connection.Dispose();
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

    /// <summary>
    /// A null <paramref name="watermark"/> used to mean "read this reader's own first-pass full load".
    /// This reader no longer full-loads on <see cref="ReadIntent.InitialLoad"/> — <c>RunExecutor</c>
    /// routes that to the Bulk Load pipeline instead, capturing the position ahead of it (see
    /// <see cref="AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad"/>) — so a null watermark here
    /// captures that same position instead and wraps it in an equivalent (empty) <see cref="ReadResult"/>.
    /// Every other call site only ever wanted this call's *position*, never its rows, so they are
    /// unaffected.
    /// </summary>
    private async Task<ReadResult> ReadAsync(string? watermark, SourceTableRef? source = null)
    {
        if (watermark is null)
        {
            var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
            var captured = await capturing.CapturePositionAsync(
                _connection, source ?? Source(), new Dictionary<string, string>(), CancellationToken.None);
            return new ReadResult(EmptyRows(), captured.Position, new ReadDiagnostics(), Bounded: null, captured.PositionTimeUtc);
        }

        return await _reader.ReadChangesAsync(
            _connection, source ?? Source(), watermark, ReadIntent.Changes,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
    }

    private static async IAsyncEnumerable<ChangeRow> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }

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
    /// Captures everything committed so far and returns the position after it. A production pass waits
    /// for the Agent capture job to come round; the fixture stopped that job and scans the log itself,
    /// so this is the same guarantee without the wait — after it returns, every prior commit is in the
    /// change table. <paramref name="after"/> is the caller's previous position, kept for readability
    /// at the call sites; the scan makes no use of it.
    /// </summary>
    private async Task<string> WaitForCaptureAsync(string after)
    {
        _ = after; // see the summary: kept for call-site readability, unused by the scan
        await CdcCaptureJob.ScanAsync(_connection);
        var max = await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None)
            ?? throw new InvalidOperationException(
                $"No max LSN after a scan. {await CdcCaptureJob.DiagnoseAsync(_connection)}.");
        return MsSqlCdcCatalog.ToWatermark(max);
    }

    /// <summary>
    /// The correctness crux phase 134 depends on. This reader no longer full-loads on
    /// <see cref="ReadIntent.InitialLoad"/> — <c>RunExecutor</c> routes that to the Bulk Load pipeline
    /// instead, capturing this reader's position (<see cref="IPositionCapturing.CapturePositionAsync"/>)
    /// before the load ever reads a row. A change committed *after* the position was captured but
    /// before anything reads from it must still arrive on the very next <see cref="ReadIntent.Changes"/>
    /// pass — get the ordering backwards and this is exactly the row that goes missing, silently.
    /// </summary>
    [Fact]
    public async Task AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        // Alice/Bob must be in the change table before CapturePositionAsync runs — otherwise the
        // capture job (simulated here by a manual scan; a real one is always this far ahead by the
        // time anything asks) hasn't caught up to them yet, the returned position is stale, and both
        // rows wrongly replay below alongside Carol.
        await WaitForCaptureAsync("0");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(captured.Position));

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (3, 'Carol');");
        await WaitForCaptureAsync(captured.Position);

        var result = await ReadAsync(captured.Position);
        var rows = await CollectAsync(result.Rows);

        var inserted = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Insert, inserted.Operation);
        Assert.Equal(3, (int)inserted["Id"]!);
        Assert.Equal("Carol", (string)inserted["Name"]!);
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

    /// <summary>
    /// <see cref="IPositionCapturing.CapturePositionAsync"/> is exactly what
    /// <see cref="ReadIntent.ChangesFromLatest"/> above adopts — the current max LSN, read via a scalar
    /// function rather than a row read — so a pass reading from the captured position afterwards finds
    /// nothing behind it.
    /// </summary>
    [Fact]
    public async Task CapturePositionAsync_ReturnsTheCurrentMaxLsn_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        await WaitForCaptureAsync(MsSqlCdcCatalog.ToWatermark(new byte[10]));

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(captured.Position));

        var next = await _reader.ReadChangesAsync(
            _connection, Source(), captured.Position, ReadIntent.Changes, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
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
            _connection, source, null, ReadIntent.InitialLoad, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("Change Data Capture is not enabled", problem.Message);
    }

    /// <summary>
    /// The single most important assertion in phase 101: <see cref="ReadIntent.ChangesFromEarliest"/>
    /// reads <c>min_lsn</c> **inclusively**, so the change sitting exactly at the feed's surviving floor
    /// is returned rather than silently skipped.
    /// <para>
    /// Two rows, then everything before the second's own LSN is pruned —
    /// <c>sys.sp_cdc_cleanup_change_table</c> is the real mechanism CDC's own retention cleanup uses,
    /// so this is not simulating a scenario, it is producing the one retention produces naturally after
    /// enough time passes. The first row is now gone from the change table; the second row's change
    /// survives at the feed's new floor, and reading <see cref="ReadIntent.ChangesFromEarliest"/>
    /// returns exactly it.
    /// </para>
    /// <para>
    /// The contrast is the point: reading from that same LSN via the *ordinary* incremental path
    /// (<c>ChangesFromEarliest</c>'s rejected predecessor — storing <c>min_lsn</c> as a watermark and
    /// reading <see cref="ReadIntent.Changes"/> from it) increments past it first and returns nothing —
    /// the second row would be silently lost forever, which is the exact defect this design exists to
    /// close.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChangesFromEarliest_ReturnsTheChangeAtTheFloor_InclusiveOfMinLsn()
    {
        var captureInstance = $"dbo_{_tableName}";

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        var afterFirst = await WaitForCaptureAsync(MsSqlCdcCatalog.ToWatermark(new byte[10]));

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'Bob');");
        var afterSecond = await WaitForCaptureAsync(afterFirst);

        // Prunes every change with __$start_lsn strictly below the second row's LSN — so the first
        // row's change is gone and the second row's is retained.
        await ExecuteAsync(
            $"EXEC sys.sp_cdc_cleanup_change_table @capture_instance = N'{captureInstance}', " +
            $"@low_water_mark = 0x{Convert.ToHexString(MsSqlCdcCatalog.FromWatermark(afterSecond))}, @threshold = 1;");

        // The feed's floor moved forward but not to an exact value: sp_cdc_cleanup_change_table rounds
        // start_lsn down to the nearest cdc.lsn_time_mapping entry, so under load it can land just shy
        // of the low-water mark rather than on it. What is guaranteed — and what the reads below rely
        // on — is that it advanced past the empty starting floor and did not overshoot the mark.
        var minLsn = await MsSqlCdcCatalog.GetMinLsnAsync(_connection, captureInstance, CancellationToken.None);
        Assert.True(MsSqlCdcCatalog.Compare(minLsn!, new byte[10]) > 0, "the floor did not advance");
        Assert.True(
            MsSqlCdcCatalog.Compare(minLsn!, MsSqlCdcCatalog.FromWatermark(afterSecond)) <= 0,
            "the floor overshot the low-water mark");

        var earliest = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, ReadIntent.ChangesFromEarliest,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var earliestRows = await CollectAsync(earliest.Rows);

        var row = Assert.Single(earliestRows);
        Assert.Equal(2, (int)row["Id"]!);
        Assert.Equal("Bob", (string)row["Name"]!);

        // The rejected design, reproduced directly: the same LSN, read via the ordinary incremental
        // path instead of the inclusive one. sys.fn_cdc_increment_lsn moves past it, and the row this
        // design exists to stop losing comes back as nothing.
        var rejected = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: afterSecond, ReadIntent.Changes,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(rejected.Rows));
    }

    /// <summary>
    /// <see cref="ReadIntent.ChangesFromLatest"/> adopts the current maximum LSN as the new position
    /// without querying the change table at all — a table can be marked already-synced, and this is the
    /// intent that does it.
    /// </summary>
    [Fact]
    public async Task ChangesFromLatest_AdoptsTheCurrentMaxLsn_WithoutReadingAnyRow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        await WaitForCaptureAsync(MsSqlCdcCatalog.ToWatermark(new byte[10]));

        var maxLsn = await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None);

        var result = await _reader.ReadChangesAsync(
            _connection, Source(), previousWatermark: null, ReadIntent.ChangesFromLatest,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);

        Assert.Empty(await CollectAsync(result.Rows));
        Assert.Equal(MsSqlCdcCatalog.ToWatermark(maxLsn!), result.NewWatermark);

        // Adopted, not merely equal by coincidence: a pass reading from this position afterwards finds
        // nothing behind it, because nothing before it was ever asked to be read.
        var next = await _reader.ReadChangesAsync(
            _connection, Source(), result.NewWatermark, ReadIntent.Changes,
            [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        Assert.Empty(await CollectAsync(next.Rows));
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
                _connection, Source(), start, ReadIntent.Changes, mappings, "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
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
        // A scan so this second instance records its start position — the fixture's stopped capture
        // job will not do it, and an InitialLoad read against an instance with no floor is an error.
        await CdcCaptureJob.ScanAsync(_connection);

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

    /// <summary>
    /// The preview has to show how the pass finds its window's end, not just the read that uses it.
    /// Showing only the read left <c>@toLsn</c> resolved to a number with nothing accounting for
    /// where it came from — the one query an operator asking "how does it check for changes?" wants.
    /// </summary>
    [Fact]
    public async Task ThePreviewShowsTheMaxLsnQuery_BeforeTheReadThatUsesIt()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'Alice');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        var statements = await _reader.DescribeAsync(
            new PreviewRequest(
                _connection, Source(), new TableRef
                {
                    ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = "Target",
                },
                [], new Dictionary<string, string>(), start, SourceColumns: [], TargetColumns: []),
            CancellationToken.None);

        Assert.Equal(2, statements.Count);
        Assert.Contains("sys.fn_cdc_get_max_lsn()", statements[0].Sql!);
        Assert.StartsWith("Ask the source for its current maximum LSN", statements[0].Title);

        // Unchanged by the addition: the read still declares both parameters as real values, so it
        // still pastes into a query tool and runs.
        Assert.StartsWith("Incremental read of changes after LSN", statements[1].Title);
        Assert.Contains("@toLsn", statements[1].DeclaredParameters!);
        Assert.Contains("@storedLsn", statements[1].DeclaredParameters!);
    }

    // ---- Row-bounded reads (phase 84) ----

    private static Dictionary<string, string> Cap(int maxRows) =>
        new() { [BoundedRead.OptionName] = maxRows.ToString() };

    /// <summary>The whole window, which after this phase is something a pass has to ask for.</summary>
    private static Dictionary<string, string> Uncapped() => Cap(0);

    private Task<ReadResult> ReadAsync(
        string? watermark, IReadOnlyDictionary<string, string> options, SourceTableRef? source = null) =>
        _reader.ReadChangesAsync(
            _connection, source ?? Source(), watermark,
            watermark is null ? ReadIntent.InitialLoad : ReadIntent.Changes,
            [], "mapping", [], options, CancellationToken.None);

    /// <summary>
    /// Scans the log and confirms all <paramref name="expected"/> pending changes are now captured, by
    /// reading the window uncapped and counting it.
    /// <para>
    /// A read does not consume anything — the watermark belongs to the caller — so peeking like this
    /// is free and leaves the position untouched for the capped pass under test. It matters because
    /// "the capped pass returned two of six" and "the capped pass returned two of the two captured so
    /// far" are the same observation, and only one is the behaviour being asserted — so the scan has
    /// to have caught up to all six before the capped read is taken.
    /// </para>
    /// </summary>
    private async Task WaitForPendingChangesAsync(string watermark, int expected, SourceTableRef? source = null)
    {
        await CdcCaptureJob.ScanAsync(_connection);

        var captured = (await CollectAsync((await ReadAsync(watermark, Uncapped(), source)).Rows)).Count;
        if (captured < expected)
            throw new InvalidOperationException(
                $"A scan captured {captured} of {expected} expected changes. " +
                $"{await CdcCaptureJob.DiagnoseAsync(_connection)}.");
    }

    /// <summary>
    /// A pass with more waiting than its cap allows stops partway and says so — and what it says is
    /// <see cref="ReadResult.WatermarkAfterRead"/>, a position it genuinely reached, not the window's
    /// end it fixed for itself before reading. Persisting the latter would record six changes as read
    /// while delivering two, and the four in between would never be seen again.
    /// </summary>
    [Fact]
    public async Task Bounded_StopsAtTheCap_AndReportsAPositionBelowTheWindowsEnd()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        // Six statements, so six transactions and six distinct __$start_lsn values. A cap of two must
        // stop at the second of them.
        for (var id = 10; id < 16; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");
        await WaitForPendingChangesAsync(start, 6);

        var result = await ReadAsync(start, Cap(2));
        var rows = await CollectAsync(result.Rows);

        Assert.Equal(2, rows.Count);
        Assert.True(
            MsSqlCdcCatalog.Compare(
                MsSqlCdcCatalog.FromWatermark(result.WatermarkAfterRead),
                MsSqlCdcCatalog.FromWatermark(result.NewWatermark)) < 0,
            "a bounded pass must record where it stopped, not the window's end");
    }

    /// <summary>
    /// The other half of the same guarantee: the position a capped pass records is one the next pass
    /// resumes cleanly from. No gap — every change arrives — and no duplicate, which for CDC means the
    /// boundary LSN is not re-read, because the next window starts at <c>fn_cdc_increment_lsn</c> of it.
    /// </summary>
    [Fact]
    public async Task Bounded_OverSeveralPasses_DeliversEveryChangeExactlyOnce()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var watermark = await SettleAsync((await ReadAsync(null)).NewWatermark);

        for (var id = 10; id < 40; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");
        await WaitForPendingChangesAsync(watermark, 30);

        var seen = new List<int>();
        for (var pass = 0; pass < 20; pass++)
        {
            var result = await ReadAsync(watermark, Cap(4));
            var rows = await CollectAsync(result.Rows);
            seen.AddRange(rows.Select(r => (int)r["Id"]!));
            watermark = result.WatermarkAfterRead;
            if (rows.Count == 0)
                break;
        }

        Assert.Equal(Enumerable.Range(10, 30), seen.Order());
        Assert.Equal(30, seen.Distinct().Count());
    }

    /// <summary>
    /// The trap this reader's ordering exists to avoid, and the one place CDC differs from Change
    /// Tracking. A CDC watermark is an LSN, so an LSN is the finest position a pass can record — and
    /// every row of one transaction shares one. Tying the cap on <c>(__$start_lsn, __$seqval)</c>,
    /// which is what the row ordering suggests, would let this pass stop after two of the four,
    /// record the transaction's LSN, and have the next pass resume strictly past it: two rows gone.
    /// <para>
    /// So the tie is on the LSN alone, and the visible consequence — asserted here rather than merely
    /// argued — is that a transaction larger than the cap comes back whole and over the cap, in
    /// <c>__$seqval</c> order. This is the all-changes instance because net changes would collapse the
    /// four updates into the one row that has no ordering to lose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bounded_DeliversATransactionLargerThanTheCapWhole_AndInOrder()
    {
        var table = $"CdcTxn_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync($"""
            EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{table}',
                 @role_name = NULL, @supports_net_changes = 0;
            """);
        // A scan so this second instance records its start position (see AnInstanceWithoutNetChanges).
        await CdcCaptureJob.ScanAsync(_connection);

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = db.DatabaseName, Schema = "dbo", Table = table,
        };

        await ExecuteAsync($"INSERT INTO dbo.[{table}] (Id, Name) VALUES (1, 'a');");
        var start = await SettleAsync((await ReadAsync(null, source)).NewWatermark, source);

        // One transaction, four changes: one __$start_lsn, four __$seqval values.
        await ExecuteAsync($"""
            BEGIN TRANSACTION;
            UPDATE dbo.[{table}] SET Name = 'b' WHERE Id = 1;
            UPDATE dbo.[{table}] SET Name = 'c' WHERE Id = 1;
            UPDATE dbo.[{table}] SET Name = 'd' WHERE Id = 1;
            UPDATE dbo.[{table}] SET Name = 'e' WHERE Id = 1;
            COMMIT TRANSACTION;
            """);
        await WaitForPendingChangesAsync(start, 4, source);

        var result = await ReadAsync(start, Cap(2), source);
        var rows = await CollectAsync(result.Rows);

        // Four, not two. WITH TIES on __$start_lsn pulls in the rest of the transaction, because
        // stopping inside it is a position this reader cannot write down.
        Assert.Equal(["b", "c", "d", "e"], rows.Select(r => (string)r["Name"]!));

        // And having delivered the whole transaction, the pass has nothing left before the window's
        // end, so it advances there rather than to a boundary it would have to re-read.
        Assert.Empty(await CollectAsync((await ReadAsync(result.WatermarkAfterRead, Cap(2), source)).Rows));
    }

    /// <summary>
    /// The cap is on unless an operator turns it off, which is the change of default this phase makes.
    /// An unbounded mapping is exactly the one nobody thought to configure, and since phase 76's 1800s
    /// default command timeout that mapping can now fail outright rather than merely run long.
    /// </summary>
    [Fact]
    public async Task WithNoOptionsAtAll_ThePassIsStillCapped()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'Bob');");
        await WaitForPendingChangesAsync(start, 1);

        // The default cap is far above anything these tests write, so what this proves is that the
        // bounded statement is the one a default-configured mapping now runs — the derived table, the
        // @maxRows parameter and the trailing position column the reader reads its stop point off —
        // and that it still returns the same rows through it. The cap's arithmetic is asserted above,
        // against a cap small enough to bite; the default's size is BoundedReadTests' business.
        var result = await ReadAsync(start, new Dictionary<string, string>());
        Assert.Single(await CollectAsync(result.Rows));

        // Not cut short, so it advances to the window's end — a quiet table's watermark has to keep
        // moving or it eventually falls below fn_cdc_get_min_lsn and expires.
        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);

        Assert.Equal(BoundedRead.OptionName, Assert.Single(_reader.Parameters).Name);
    }

    /// <summary>Zero is the escape hatch, and it has to reach the statement: an operator who has read
    /// the description and typed 0 gets the pre-phase-84 read back.</summary>
    [Fact]
    public async Task Uncapped_ReadsTheWholeWindow()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        for (var id = 10; id < 15; id++)
            await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES ({id}, 'r{id}');");
        await WaitForPendingChangesAsync(start, 5);

        var result = await ReadAsync(start, Uncapped());

        Assert.Equal(5, (await CollectAsync(result.Rows)).Count);
        Assert.Equal(result.NewWatermark, result.WatermarkAfterRead);
    }

    // ---- The commit time captured beside the LSN (phase 87) -------------------------------------

    /// <summary>
    /// The read returns the engine's own time for the LSN it is about to store, mapped on the
    /// connection this pass already had open. Without it, the applied side of every CDC lag figure
    /// had to be re-asked of the source on each request — which is what made a status screen a
    /// recurring load on the database it merely reported about.
    /// </summary>
    [Fact]
    public async Task AReadCarriesTheEnginesCommitTimeForTheLsnItIsAboutToStore()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (2, 'next');");
        await WaitForPendingChangesAsync(start, 1);

        var result = await ReadAsync(start);
        await CollectAsync(result.Rows);

        // The independent mapping of the stored position is what proves the reader asked about the
        // LSN it is persisting rather than some other point in the window.
        Assert.NotNull(result.WatermarkTimeAfterRead);
        Assert.Equal(
            await MsSqlCdcCatalog.MapLsnToTimeAsync(
                _connection, MsSqlCdcCatalog.FromWatermark(result.WatermarkAfterRead), CancellationToken.None),
            result.WatermarkTimeAfterRead);
    }

    /// <summary>
    /// **A quiet mapping still refreshes its cached time.** The nothing-new branch returns the stored
    /// position unchanged, and if it returned no time with it, a caught-up mapping — including one
    /// whose row predates the column — would never acquire one and would report "no lag data" for
    /// ever.
    /// </summary>
    [Fact]
    public async Task AReadWithNothingNew_StillCarriesATimeForTheUnchangedPosition()
    {
        await ExecuteAsync($"INSERT INTO dbo.[{_tableName}] (Id, Name) VALUES (1, 'seed');");
        var start = await SettleAsync((await ReadAsync(null)).NewWatermark);

        var result = await ReadAsync(start);

        Assert.Empty(await CollectAsync(result.Rows));
        Assert.Equal(start, result.WatermarkAfterRead);
        Assert.NotNull(result.WatermarkTimeAfterRead);
    }
}
