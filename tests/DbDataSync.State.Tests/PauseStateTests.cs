namespace DbDataSync.State.Tests;

/// <summary>
/// The current-state columns and the audit trail behind them — phase 64. The history has no reader in
/// the product yet (its viewer is a separate follow-up), so these tests are the only thing standing
/// between "it is recorded" and "it is recorded correctly".
/// </summary>
public sealed class PauseStateTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-pause-tests-").FullName;
    private readonly TaskRunStore _store;

    public PauseStateTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void ATaskNobodyHasTouched_IsNotPaused()
    {
        Assert.False(_store.IsPaused("crm-sync"));
        Assert.Equal((false, null), _store.GetPauseState("crm-sync"));
        Assert.Empty(_store.GetPauseHistory("crm-sync"));
    }

    [Fact]
    public void Pausing_ATaskThatHasNeverRun_Works()
    {
        // No Tasks row exists until a worker upserts one, and holding something before its first run
        // is ordinary — so SetPaused inserts rather than assuming.
        _store.SetPaused("crm-sync", paused: true, note: "waiting on the DBA", performedBy: "ada");

        Assert.True(_store.IsPaused("crm-sync"));
        Assert.Equal((true, "waiting on the DBA"), _store.GetPauseState("crm-sync"));
    }

    [Fact]
    public void Pausing_DoesNotDisturbEnabled_AndUpsertTaskDoesNotDisturbPaused()
    {
        _store.UpsertTask("crm-sync", enabled: true);
        _store.SetPaused("crm-sync", paused: true, note: null, performedBy: "ada");

        // The worker's own mirror refresh runs on every start, and must not quietly resume anything.
        _store.UpsertTask("crm-sync", enabled: true);

        Assert.True(_store.IsPaused("crm-sync"));
    }

    [Fact]
    public void Resuming_ClearsTheHold()
    {
        _store.SetPaused("crm-sync", paused: true, note: "index rebuild", performedBy: "ada");
        _store.SetPaused("crm-sync", paused: false, note: null, performedBy: "ada");

        Assert.False(_store.IsPaused("crm-sync"));
        Assert.Equal((false, null), _store.GetPauseState("crm-sync"));
    }

    [Fact]
    public void ANoteCanBeEdited_Kept_OrCleared_OnAnyAction()
    {
        // Nothing about the note is automatic — the popup asks every time, so all three outcomes have
        // to be expressible, including clearing it while staying paused.
        _store.SetPaused("crm-sync", paused: true, note: "first", performedBy: "ada");
        _store.SetPaused("crm-sync", paused: true, note: "second", performedBy: "ada");
        Assert.Equal("second", _store.GetPauseState("crm-sync").Note);

        _store.SetPaused("crm-sync", paused: true, note: "", performedBy: "ada");
        Assert.Equal("", _store.GetPauseState("crm-sync").Note);
    }

    [Fact]
    public void EveryActionIsRecorded_InOrder_WithWhoDidIt()
    {
        _store.SetPaused("crm-sync", paused: true, note: "index rebuild", performedBy: "ada");
        _store.SetPaused("crm-sync", paused: false, note: null, performedBy: "grace");
        _store.SetPaused("crm-sync", paused: true, note: "again", performedBy: "ada");

        var history = _store.GetPauseHistory("crm-sync");

        Assert.Equal(3, history.Count);
        Assert.Equal([PauseActions.Paused, PauseActions.Resumed, PauseActions.Paused],
            history.Select(e => e.Action).Reverse());
        Assert.Equal(["ada", "grace", "ada"], history.Select(e => e.PerformedBy).Reverse());
        Assert.Equal("again", history[0].Note);
        Assert.Null(history[1].Note);
    }

    [Fact]
    public void HistoryIsPerTask()
    {
        _store.SetPaused("crm-sync", paused: true, note: null, performedBy: "ada");
        _store.SetPaused("erp-sync", paused: true, note: null, performedBy: "ada");

        Assert.Single(_store.GetPauseHistory("crm-sync"));
        Assert.True(_store.IsPaused("erp-sync"));
        Assert.False(_store.IsPaused("unrelated"));
    }

    [Fact]
    public void HistoryRespectsItsLimit_MostRecentFirst()
    {
        for (var i = 0; i < 5; i++)
            _store.SetPaused("crm-sync", paused: i % 2 == 0, note: $"note {i}", performedBy: "ada");

        var history = _store.GetPauseHistory("crm-sync", limit: 2);

        Assert.Equal(2, history.Count);
        Assert.Equal("note 4", history[0].Note);
        Assert.Equal("note 3", history[1].Note);
    }
}
