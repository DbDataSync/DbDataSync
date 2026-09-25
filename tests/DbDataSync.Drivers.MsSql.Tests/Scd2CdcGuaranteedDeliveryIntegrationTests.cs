using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// Phase 132's actual reproduction: <see cref="MsSqlCdcReader"/> → <see cref="BatchInsertStagingProvider"/>
/// → <see cref="Scd2Writer"/>, against a real server with CDC enabled, exercising the exact defect the
/// phase doc confirmed by code inspection — a key changed twice between reads stages two rows in one
/// pass, and before this phase both computed the identical pass-wide surrogate key and collided on the
/// target's own primary key.
/// <para>
/// The capture instance is created with <c>@supports_net_changes = 0</c> so the reader falls back to
/// <c>fn_cdc_get_all_changes_*</c> — net changes would collapse repeated updates to one key into a
/// single row, which is exactly the coalescing this phase's guarantee exists to stop, and would make
/// the duplicate-key path in <c>Scd2Writer</c> untestable from here.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Scd2CdcGuaranteedDeliveryIntegrationTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlCdcReader _reader = new();
    private readonly BatchInsertStagingProvider _staging = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);
    private readonly Scd2Writer _writer = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);

    private SqlConnection _sourceConnection = null!;
    private SqlConnection _targetConnection = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    private const string MappingName = "scd2-cdc-guaranteed-delivery";

    public async Task InitializeAsync()
    {
        // Unpooled: CDC's own scan checks the log reader out to the session and does not check it back
        // in, the same reason MsSqlCdcReaderTests uses one — see CdcCaptureJob's own docs.
        _sourceConnection = db.OpenConnection(pooled: false);
        _targetConnection = db.OpenConnection();

        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"Scd2CdcSrc_{suffix}";
        _targetTable = $"Scd2CdcTgt_{suffix}";

        // Note is captured by CDC (a capture instance covers every column unless told otherwise) but
        // never mapped — the column the "middle change touches no mapped column" scenario changes.
        await ExecuteAsync(_sourceConnection, $"""
            CREATE TABLE dbo.[{_sourceTable}] (
                Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Note NVARCHAR(50) NULL);
            """);

        await CdcCaptureJob.EnableDbAsync(_sourceConnection);
        await EnableCaptureWithRetryAsync();
        await CdcCaptureJob.ScanAsync(_sourceConnection);

        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{_targetTable}] (
                {HistorizedColumns.SurrogateKey} VARCHAR(200) NOT NULL PRIMARY KEY,
                Id INT NOT NULL,
                Name NVARCHAR(50) NOT NULL,
                {HistorizedColumns.ValidFrom} DATETIME2 NOT NULL,
                {HistorizedColumns.ValidTo} DATETIME2 NULL,
                {HistorizedColumns.IsCurrent} BIT NOT NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        try { await CdcCaptureJob.StopCaptureJobAsync(_sourceConnection); }
        catch { /* best-effort teardown, matching MsSqlCdcReaderTests */ }
        try { await CdcCaptureJob.ReleaseLogReaderAsync(_sourceConnection); }
        catch { /* best-effort teardown, matching MsSqlCdcReaderTests */ }
        _sourceConnection.Dispose();
        _targetConnection.Dispose();
    }

    // ---- CDC setup, mirroring MsSqlCdcReaderTests' own retry (a shared private helper would have had
    // to move to internal on that class for no reason but this one test file) ----------------------

    private async Task EnableCaptureWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync(_sourceConnection, $"""
                    EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{_sourceTable}',
                         @role_name = NULL, @supports_net_changes = 0;
                    """);
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
                if (await MsSqlCdcCatalog.FindCaptureInstanceAsync(
                        _sourceConnection, "dbo", _sourceTable, CancellationToken.None) is not null)
                    return;
            }
        }
    }

    private static bool IsDeadlock(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number == 1205)
        || ex.Message.Contains("was deadlocked on lock resources", StringComparison.Ordinal);

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = _sourceTable,
    };

    private TableRef Target() => new()
    {
        ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable,
    };

    private static List<CachedColumn> TargetColumns() =>
    [
        new(HistorizedColumns.SurrogateKey, "varchar(200)", false, true, false),
        new("Id", "int", false, false, false),
        new("Name", "nvarchar(50)", false, false, false),
        new(HistorizedColumns.ValidFrom, "datetime2", false, false, false),
        new(HistorizedColumns.ValidTo, "datetime2", true, false, false),
        new(HistorizedColumns.IsCurrent, "bit", false, false, false),
    ];

    private static Dictionary<string, string> WriterOptions() =>
        new() { [Scd2Writer.NaturalKeyOption] = "Id" };

    /// <summary>Reads, stages and applies one pass, returning the watermark to resume from next and the
    /// staged set (so a test can assert <see cref="StagedChangeSet.HasChangeOrdering"/> directly).</summary>
    private async Task<(string Watermark, StagedChangeSet Staged, WriteResult Written)> RunPassAsync(
        string? watermark, ReadIntent intent)
    {
        var read = await _reader.ReadChangesAsync(
            _sourceConnection, Source(), watermark, intent, Mappings, MappingName, [], [],new Dictionary<string, IReadOnlyList<CachedColumn>>(),
            new Dictionary<string, string>(), CancellationToken.None);
        var staged = await _staging.StageAsync(
            _targetConnection, Target(), read.Rows, Mappings, MappingName, TargetColumns(),
            new Dictionary<string, string>(), CancellationToken.None);
        try
        {
            var written = await _writer.ApplyAsync(
                _targetConnection, Target(), staged, Mappings, MappingName, TargetColumns(),
                WriterOptions(), CancellationToken.None);
            return (read.NewWatermark, staged, written);
        }
        finally
        {
            await _staging.CleanupAsync(_targetConnection, staged, CancellationToken.None);
        }
    }

    /// <summary>
    /// Phase 134: an initial load no longer goes through this reader — <c>RunExecutor</c> routes it to
    /// the Bulk Load pipeline (a plain table scan, <c>BatchReloadReader</c>) instead, capturing this
    /// reader's own position ahead of it via <see cref="IPositionCapturing"/> rather than reading
    /// through it (see <c>MsSqlCdcReaderTests</c>' own established idiom for this exact seam —
    /// <c>ReadIntent.InitialLoad</c> with a null watermark now throws here, per phase 134's own "Known
    /// follow-up"). Mirrored here rather than a bare position capture, because unlike that idiom's own
    /// call sites this test's initial rows are not thrown away: the incremental pass's own asserted
    /// versions (<c>id1 = ["a","b","c"]</c>) start from them, so they are staged and written directly —
    /// as a real Bulk Load's <c>BatchReloadReader</c> would produce them, a plain table scan with no
    /// ordering columns (hence <see cref="StagedChangeSet.HasChangeOrdering"/> is still false here).
    /// </summary>
    private async Task<string> InitialLoadAsync()
    {
        var schema = new ChangeSchema(["Id", "Name"]);
        var rows = new List<ChangeRow>();
        await using (var cmd = _sourceConnection.CreateCommand())
        {
            cmd.CommandText = $"SELECT Id, Name FROM dbo.[{_sourceTable}] ORDER BY Id;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(new ChangeRow(ChangeOperation.Insert, schema, [reader.GetInt32(0), reader.GetString(1)]));
        }

        var staged = await _staging.StageAsync(
            _targetConnection, Target(), ToAsyncEnumerable(rows), Mappings, MappingName, TargetColumns(),
            new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(staged.HasChangeOrdering);
        try
        {
            await _writer.ApplyAsync(
                _targetConnection, Target(), staged, Mappings, MappingName, TargetColumns(),
                WriterOptions(), CancellationToken.None);
        }
        finally
        {
            await _staging.CleanupAsync(_targetConnection, staged, CancellationToken.None);
        }

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _sourceConnection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        return captured.Position;
    }

    private static async IAsyncEnumerable<ChangeRow> ToAsyncEnumerable(List<ChangeRow> rows)
    {
        await Task.CompletedTask;
        foreach (var row in rows)
            yield return row;
    }

    private sealed record Version(string VersionKey, string Name, DateTime ValidFrom, DateTime? ValidTo, bool IsCurrent);

    private async Task<List<Version>> ReadVersionsAsync(int id)
    {
        await using var cmd = _targetConnection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {HistorizedColumns.SurrogateKey}, Name, {HistorizedColumns.ValidFrom}, {HistorizedColumns.ValidTo}, {HistorizedColumns.IsCurrent}
            FROM dbo.[{_targetTable}]
            WHERE Id = {id}
            ORDER BY {HistorizedColumns.ValidFrom};
            """;
        var results = new List<Version>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new Version(
                reader.GetString(0), reader.GetString(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetBoolean(4)));
        }
        return results;
    }

    /// <summary>
    /// The whole point of the phase, in one pass: a duplicate key with a real value transition every
    /// time (Id 1), a duplicate key whose middle change touches no mapped column (Id 3), and a singleton
    /// key in the very same pass (Id 2) — proving the bulk path still carries every key the duplicate
    /// machinery does not have to touch, unaffected.
    /// </summary>
    // These tests assert that two changes to one key were mapped to two distinct times (a delete before its
    // re-insert, an update before the next). A scan between them guarantees two distinct mapping *points*, not
    // two distinct *times* — see CdcCaptureJob.ScanUntilPastAsync's own doc comment for why a fixed delay
    // between the operations turned out not to be enough, and why these tests wait on that instead of on time.

    // Disabled 2026-09-23, re-enabled 2026-09-24 after clearing the bar the disable decision itself set —
    // see architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md's "20-run bar"
    // section for the fix (CdcCaptureJob.ReleaseLogReaderAsync moved to test teardown) and the actual
    // 20-consecutive-solo-run result for this test.
    [Fact]
    public async Task APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Name, Note) VALUES
                (1, 'a', 'n0'), (2, 'p', 'n0'), (3, 'x', 'n0');
            """);

        // A full load's watermark is the *current* max LSN as of the read — CDC has also captured
        // these three inserts (they happened after capture was enabled), and scanning first is what
        // keeps the incremental pass below from re-seeing them as phantom duplicate rows for every key,
        // Id 2 included, which would otherwise no longer be the singleton this pass needs it to be.
        await CdcCaptureJob.ScanAsync(_sourceConnection);
        var watermark = await InitialLoadAsync();

        // Two updates to Id 1 between reads — the exact scenario the plan doc confirmed crashes today.
        // Scanning between them (rather than once at the end) is what gives them two distinct
        // transactions and, in the all-changes fallback this fixture forces, two distinct __$start_lsn
        // values for Scd2Writer to key its per-row processing on.
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'b' WHERE Id = 1;");
        var afterFirstUpdate = await CdcCaptureJob.ScanAsync(_sourceConnection);

        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'c' WHERE Id = 1;");
        await CdcCaptureJob.ScanUntilPastAsync(_sourceConnection, afterFirstUpdate);

        // A singleton key in the very same pass.
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'q' WHERE Id = 2;");

        // A third key: three changes, the middle one touching only the unmapped Note column.
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'y' WHERE Id = 3;");
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Note = 'n1' WHERE Id = 3;");
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'z' WHERE Id = 3;");
        await CdcCaptureJob.ScanAsync(_sourceConnection);

        // The apply itself: this is what would have thrown a primary-key violation before this phase —
        // both of Id 1's staged rows, and two of Id 3's three, computing the identical pass-wide
        // surrogate key.
        var (_, staged, written) = await RunPassAsync(watermark, ReadIntent.Changes);
        Assert.True(staged.HasChangeOrdering);

        // Id 1: two ordered versions produced by this pass, each closing/opening at its own change's
        // mapped time — not the pass time, and not each other's.
        //
        // Asserted as internal consistency rather than against an independently re-queried
        // sys.fn_cdc_map_lsn_to_time value: a real bug was found writing this test the other way. That
        // function *interpolates* between points cdc.lsn_time_mapping holds (per its own doc comment),
        // and a later sp_cdc_scan can add mapping points that shift the interpolation for an *already
        // committed* LSN — so a value read independently before the pass can legitimately differ by a
        // few milliseconds from what the pass itself reads for the very same LSN later on. What must
        // hold — and is what "not the pass time" actually means here — is that the closing edge and the
        // opening edge of one transition share the exact same value (both come from the one staged row's
        // own ChangedAtColumn), and that the two transitions do not share a value with each other (which
        // a single pass-wide @now would).
        var id1 = await ReadVersionsAsync(1);
        Assert.Equal(["a", "b", "c"], id1.Select(v => v.Name));
        Assert.False(id1[0].IsCurrent);
        Assert.False(id1[1].IsCurrent);
        Assert.True(id1[2].IsCurrent);
        Assert.Equal(id1[0].ValidTo, id1[1].ValidFrom);
        Assert.Equal(id1[1].ValidTo, id1[2].ValidFrom);
        Assert.True(id1[0].ValidTo != id1[1].ValidTo,
            $"each update landed at its own mapped time; got {id1[0].ValidTo:O} and {id1[1].ValidTo:O}");
        // The actual collision fix: OrderingColumn|naturalKey, not the pass-wide prefix — the two
        // versions this pass opened for Id 1 could never have shared this format's value.
        Assert.Matches("^[0-9A-Fa-f]{40}\\|1$", id1[1].VersionKey);
        Assert.Matches("^[0-9A-Fa-f]{40}\\|1$", id1[2].VersionKey);
        Assert.NotEqual(id1[1].VersionKey, id1[2].VersionKey);

        // Id 2: unaffected by the duplicate-key machinery — a singleton row in this pass still gets the
        // pass-wide prefix format, exactly as it would have before this phase.
        var id2 = await ReadVersionsAsync(2);
        Assert.Equal(["p", "q"], id2.Select(v => v.Name));
        Assert.False(id2[0].IsCurrent);
        Assert.True(id2[1].IsCurrent);
        Assert.Matches(@"^\d{17}-2$", id2[1].VersionKey);

        // Id 3: three staged changes, two versions opened — the middle one (Note only) is not a value
        // transition on any mapped column, so it correctly opens nothing, the same way a single-row
        // pass over an unchanged value already would.
        var id3 = await ReadVersionsAsync(3);
        Assert.Equal(["x", "y", "z"], id3.Select(v => v.Name));
        Assert.False(id3[0].IsCurrent);
        Assert.False(id3[1].IsCurrent);
        Assert.True(id3[2].IsCurrent);
        Assert.Matches("^[0-9A-Fa-f]{40}\\|3$", id3[1].VersionKey);
        Assert.Matches("^[0-9A-Fa-f]{40}\\|3$", id3[2].VersionKey);

        // Versions opened this pass: Id 1's two, Id 2's one, Id 3's two — five, not the seven staged
        // rows (2 + 1 + 3 - the Note-only no-op is staged but opens nothing).
        Assert.Equal(5, written.RowsWritten);
    }

    /// <summary>
    /// Phase 145's two new edge cases, neither of which phase 132's row-by-row loop needed a test for —
    /// its per-row state made them fall out — and both of which are real defects available in a
    /// window-function rewrite.
    /// <para>
    /// **A duplicate key whose first staged row is a delete** (Id 4, deleted and re-inserted between
    /// reads). The delete has no version of its own to open, but it is still what ends the version the
    /// target already had, so it has to stay in the ordering while being excluded from the insert. The
    /// row after it must open whatever its values are — there is no open version left for it to match
    /// against — which is the one case the <c>LAG</c> comparison cannot answer on values alone.
    /// </para>
    /// <para>
    /// **A duplicate key whose last staged row is a delete** (Id 5, updated and then deleted). The key
    /// ends the pass with no open version at all, unlike every other duplicate-key scenario: the
    /// version this pass opened is opened already closed, by the delete that follows it.
    /// </para>
    /// </summary>
    // Disabled 2026-09-23, re-enabled 2026-09-24 — same fix and same cleared bar as the sibling test above;
    // see architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md's "20-run bar"
    // section.
    [Fact]
    public async Task ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid()
    {
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Name, Note) VALUES (4, 'd0', 'n0'), (5, 'e0', 'n0');
            """);

        await CdcCaptureJob.ScanAsync(_sourceConnection);
        var watermark = await InitialLoadAsync();

        // Id 4: gone, then back — a delete first, an insert second. Scanning between them is what gives
        // the two rows distinct __$start_lsn values, the same reason the test above scans between its
        // two updates.
        await ExecuteAsync(_sourceConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 4;");
        var afterDelete = await CdcCaptureJob.ScanAsync(_sourceConnection);
        await ExecuteAsync(_sourceConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Name, Note) VALUES (4, 'd1', 'n0');
            """);
        await CdcCaptureJob.ScanUntilPastAsync(_sourceConnection, afterDelete);

        // Id 5: changed, then gone.
        await ExecuteAsync(_sourceConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'e1' WHERE Id = 5;");
        var afterUpdate = await CdcCaptureJob.ScanAsync(_sourceConnection);
        await ExecuteAsync(_sourceConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 5;");
        await CdcCaptureJob.ScanUntilPastAsync(_sourceConnection, afterUpdate);

        var (_, staged, written) = await RunPassAsync(watermark, ReadIntent.Changes);
        Assert.True(staged.HasChangeOrdering);

        // Id 4: the delete ends 'd0' — at its own time, not the re-insert's — and opens nothing; the
        // insert after it opens 'd1' as the current version. The gap between the two is the period the
        // row did not exist at the source, and recording it is the point of an SCD2 target.
        var id4 = await ReadVersionsAsync(4);
        Assert.Equal(["d0", "d1"], id4.Select(v => v.Name));
        Assert.False(id4[0].IsCurrent);
        Assert.True(id4[1].IsCurrent);
        Assert.NotNull(id4[0].ValidTo);
        Assert.Null(id4[1].ValidTo);
        Assert.True(id4[0].ValidTo < id4[1].ValidFrom,
            $"the delete closed 'd0' before the re-insert opened 'd1'; d0 closed at {id4[0].ValidTo:O}, d1 opened at {id4[1].ValidFrom:O}");
        Assert.Matches("^[0-9A-Fa-f]{40}\\|4$", id4[1].VersionKey);

        // Id 5: two versions, neither current — the update's own version is opened already closed, by
        // the delete that follows it in the same pass.
        var id5 = await ReadVersionsAsync(5);
        Assert.Equal(["e0", "e1"], id5.Select(v => v.Name));
        Assert.All(id5, v => Assert.False(v.IsCurrent));
        Assert.All(id5, v => Assert.NotNull(v.ValidTo));
        // The update is one transition: it ends 'e0' and begins 'e1' at the same moment.
        Assert.Equal(id5[0].ValidTo, id5[1].ValidFrom);
        // ... and the delete ends 'e1' later, at its own moment rather than the update's.
        Assert.True(id5[1].ValidTo != id5[1].ValidFrom,
            $"the delete ended 'e1' at its own moment; e1 opened at {id5[1].ValidFrom:O} and closed at {id5[1].ValidTo:O}");
        Assert.Matches("^[0-9A-Fa-f]{40}\\|5$", id5[1].VersionKey);

        // Two versions opened this pass — Id 4's re-insert and Id 5's update. The two deletes open
        // nothing, which is why this is 2 and not the 4 rows that were staged.
        Assert.Equal(2, written.RowsWritten);
    }
}
