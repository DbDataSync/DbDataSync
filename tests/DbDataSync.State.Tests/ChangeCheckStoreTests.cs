namespace DbDataSync.State.Tests;

/// <summary>
/// The scheduler's change-check history — see phase 75. An append-only record of what the polling
/// gate saw and when, kept because the gate suppresses work and "the source was quiet" therefore has
/// to stay distinguishable from "we stopped looking".
/// </summary>
public sealed class ChangeCheckStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-checks-tests-").FullName;
    private readonly StateDatabase _database;
    private readonly ChangeCheckStore _store;

    public ChangeCheckStoreTests()
    {
        _database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new ChangeCheckStore(_database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Backdated by direct UPDATE, because Record only knows "now" — the same shape
    /// RunPruningTests uses on TaskRuns, and for the same reason.</summary>
    private void Backdate(TimeSpan age)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "UPDATE ChangeCheckHistory SET CheckedAtUtc = $checked;");
        cmd.Bind(_database, "checked", (DateTimeOffset.UtcNow - age).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void EveryCheckIsKept_NotJustTheMostRecent()
    {
        _store.Record("src", "App", "MsSqlChangeTracking", "10");
        _store.Record("src", "App", "MsSqlChangeTracking", "10");
        _store.Record("src", "App", "MsSqlChangeTracking", "11");

        // Append-only, including the repeat. Two identical readings an interval apart is the shape of
        // a genuinely quiet source, and collapsing them would erase the evidence that anybody looked.
        Assert.Equal(3, _store.ListChecks().Count);
        Assert.Equal(["11", "10", "10"], _store.ListChecks().Select(c => c.Value));
    }

    [Fact]
    public void CdcAndChangeTracking_AreSeparateRowsForOneDatabase()
    {
        _store.Record("src", "App", "MsSqlChangeTracking", "42");
        _store.Record("src", "App", "MsSqlCdc", "0000002A000000AB0003");

        var checks = _store.ListChecks();
        Assert.Equal("42", Assert.Single(checks, c => c.SourceKind == "MsSqlChangeTracking").Value);
        Assert.Equal(
            "0000002A000000AB0003",
            Assert.Single(checks, c => c.SourceKind == "MsSqlCdc").Value);
    }

    [Fact]
    public void ANullValueIsRecorded_AndIsNotTheSameAsNoRow()
    {
        _store.Record("src", "App", "MsSqlCdc", null);

        // fn_cdc_get_max_lsn() answering null means the capture job has not run. That is a real
        // observation — the source was reachable and had no position — and losing it would leave the
        // history unable to say whether anybody asked.
        Assert.Null(Assert.Single(_store.ListChecks()).Value);
    }

    [Fact]
    public void PruneChecks_DeletesOnlyRowsPastTheWindow()
    {
        _store.Record("src", "App", "MsSqlChangeTracking", "old");
        Backdate(TimeSpan.FromDays(30));
        _store.Record("src", "App", "MsSqlChangeTracking", "fresh");

        Assert.Equal(1, _store.PruneChecks(TimeSpan.FromDays(7)));
        Assert.Equal("fresh", Assert.Single(_store.ListChecks()).Value);
    }

    [Fact]
    public void PruneChecks_WithNoWindow_DeletesNothing()
    {
        _store.Record("src", "App", "MsSqlChangeTracking", "1");
        Backdate(TimeSpan.FromDays(3650));

        // Null is "keep everything", matching how the run caps read a configured 0 — between two
        // readings of an unset knob, the one that does not delete ten years of history wins.
        Assert.Equal(0, _store.PruneChecks(maxAge: null));
        Assert.Single(_store.ListChecks());
    }
}
