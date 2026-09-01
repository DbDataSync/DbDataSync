namespace DataSync.State.Tests;

/// <summary>
/// Retention, at the level that actually does the deleting. The cases that matter are the ones where
/// pruning would destroy something: an in-flight run, a quiet mapping beside a noisy one, and log
/// lines whose run went without them.
/// </summary>
public sealed class RunPruningTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-prune-tests-").FullName;
    private readonly StateDatabase _database;
    private readonly TaskRunStore _store;
    private readonly WorkQueueStore _queue;
    private readonly LogWriter _logs;

    public RunPruningTests()
    {
        _database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(_database);
        _queue = new WorkQueueStore(_database);
        _logs = new LogWriter(_database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>
    /// A finished run, backdated so age-based pruning has something to bite on.
    /// <para>
    /// Backdated by direct UPDATE because EnqueuedAtUtc is set by the queue, which quite reasonably only
    /// knows "now". Each run gets a distinct segment label because Enqueue deduplicates on the
    /// in-flight unique index — enqueuing the same (task, kind, mapping, segment) twice returns the
    /// first one's RunId rather than a second row, which is correct for the queue and would silently
    /// give these tests one run where they asked for ten.
    /// </para>
    /// </summary>
    private Guid CompletedRun(string mapping, TimeSpan age = default, string task = "crm-sync")
    {
        var runId = _queue.Enqueue(task, RunKind.Primary, mapping, segmentLabel: Guid.NewGuid().ToString("N"));
        _store.BeginRun(runId, pid: 1);
        _store.CompleteRun(runId, RunStatus.Succeeded, 1, 1, errorSummary: null);

        if (age > TimeSpan.Zero)
            Backdate(runId, age);

        return runId;
    }

    private Guid RunningRun(string mapping, TimeSpan age = default)
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, mapping, segmentLabel: Guid.NewGuid().ToString("N"));
        _store.BeginRun(runId, pid: 1);
        if (age > TimeSpan.Zero)
            Backdate(runId, age);
        return runId;
    }

    private void Backdate(Guid runId, TimeSpan age)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, "UPDATE TaskRuns SET EnqueuedAtUtc = $enqueued WHERE RunId = $runId;");
        cmd.Bind(_database, "enqueued", (DateTimeOffset.UtcNow - age).ToString("O"));
        cmd.Bind(_database, "runId", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    private bool Exists(Guid runId) => _store.GetRun(runId) is not null;

    [Fact]
    public void WithNeitherCap_NothingIsPruned()
    {
        var old = CompletedRun("orders", TimeSpan.FromDays(400));

        Assert.Equal(0, _store.PruneRuns(maxAge: null, maxPerMapping: null));
        Assert.True(Exists(old));
    }

    [Fact]
    public void TheAgeCap_PrunesOlderRunsAndKeepsRecentOnes()
    {
        var old = CompletedRun("orders", TimeSpan.FromDays(100));
        var recent = CompletedRun("orders", TimeSpan.FromDays(5));

        Assert.Equal(1, _store.PruneRuns(TimeSpan.FromDays(90), maxPerMapping: null));

        Assert.False(Exists(old));
        Assert.True(Exists(recent));
    }

    [Fact]
    public void TheCountCap_KeepsExactlyTheMostRecentRuns()
    {
        // Ages descending so "most recent" is unambiguous — two rows sharing a timestamp would make
        // which one survives a coin toss, and the test would be flaky rather than wrong.
        var runs = Enumerable.Range(1, 6)
            .Select(i => CompletedRun("orders", TimeSpan.FromMinutes(i)))
            .ToList();

        Assert.Equal(3, _store.PruneRuns(maxAge: null, maxPerMapping: 3));

        // runs[0] is the newest (1 minute old), runs[5] the oldest.
        Assert.True(Exists(runs[0]));
        Assert.True(Exists(runs[2]));
        Assert.False(Exists(runs[3]));
        Assert.False(Exists(runs[5]));
    }

    /// <summary>
    /// The reason the cap is per mapping rather than global: a busy mapping must not be able to evict
    /// a quiet one's history, which is exactly the history somebody wants when the quiet one breaks.
    /// </summary>
    [Fact]
    public void TheCountCap_IsPerMapping_SoANoisyMappingCannotCrowdOutAQuietOne()
    {
        var quiet = CompletedRun("audit-log", TimeSpan.FromMinutes(30));
        foreach (var i in Enumerable.Range(1, 10))
            CompletedRun("orders", TimeSpan.FromMinutes(i));

        _store.PruneRuns(maxAge: null, maxPerMapping: 2);

        Assert.True(Exists(quiet));
        Assert.Equal(2, _store.GetRunHistory("crm-sync").Count(r => r.MappingName == "orders"));
        Assert.Equal(1, _store.GetRunHistory("crm-sync").Count(r => r.MappingName == "audit-log"));
    }

    [Fact]
    public void TheCountCap_IsPerTaskToo()
    {
        foreach (var i in Enumerable.Range(1, 4))
            CompletedRun("orders", TimeSpan.FromMinutes(i));
        foreach (var i in Enumerable.Range(1, 4))
            CompletedRun("orders", TimeSpan.FromMinutes(i), task: "other-sync");

        _store.PruneRuns(maxAge: null, maxPerMapping: 2);

        Assert.Equal(2, _store.GetRunHistory("crm-sync").Count);
        Assert.Equal(2, _store.GetRunHistory("other-sync").Count);
    }

    /// <summary>
    /// Deleting the row underneath a worker that is still writing to it would turn a slow pass into a
    /// lost one. Age is not a reason to prune something that has not finished.
    /// </summary>
    [Fact]
    public void ARunThatHasNotFinished_IsNeverPruned()
    {
        var running = RunningRun("orders", TimeSpan.FromDays(400));
        var queued = _queue.Enqueue("crm-sync", RunKind.Primary, "orders", segmentLabel: Guid.NewGuid().ToString("N"));
        Backdate(queued, TimeSpan.FromDays(400));

        _store.PruneRuns(TimeSpan.FromDays(1), maxPerMapping: 1);

        Assert.True(Exists(running));
        Assert.True(Exists(queued));
    }

    [Fact]
    public void PruningARun_TakesItsLogLinesWithIt()
    {
        var doomed = CompletedRun("orders", TimeSpan.FromDays(100));
        var kept = CompletedRun("orders", TimeSpan.FromDays(1));
        _logs.Log(doomed, LogSeverity.Info, "this run is about to be pruned");
        _logs.Log(kept, LogSeverity.Info, "this one is not");
        _logs.Flush();

        _store.PruneRuns(TimeSpan.FromDays(90), maxPerMapping: null);

        // Logs has no enforced foreign key here, so nothing else would ever clean these up.
        Assert.Empty(_logs.GetLogs(doomed));
        Assert.Single(_logs.GetLogs(kept));
    }

    /// <summary>Both caps together: failing either is enough, and failing neither is enough to
    /// survive.</summary>
    [Fact]
    public void BothCaps_ApplyIndependently()
    {
        var oldButRecentEnoughByCount = CompletedRun("orders", TimeSpan.FromDays(100));
        var newButBeyondTheCount = CompletedRun("orders", TimeSpan.FromMinutes(3));
        var survivor = CompletedRun("orders", TimeSpan.FromMinutes(1));

        // Cap of 2 keeps the two newest by start time (survivor, newButBeyondTheCount); the age cap
        // then also condemns the 100-day-old one, which the count cap had already condemned.
        _store.PruneRuns(TimeSpan.FromDays(90), maxPerMapping: 2);

        Assert.False(Exists(oldButRecentEnoughByCount));
        Assert.True(Exists(newButBeyondTheCount));
        Assert.True(Exists(survivor));

        // Now tighten the count alone: the age-compliant middle row goes for the other reason.
        _store.PruneRuns(maxAge: null, maxPerMapping: 1);

        Assert.False(Exists(newButBeyondTheCount));
        Assert.True(Exists(survivor));
    }

    [Fact]
    public void PruningIsIdempotent()
    {
        foreach (var i in Enumerable.Range(1, 5))
            CompletedRun("orders", TimeSpan.FromMinutes(i));

        Assert.Equal(3, _store.PruneRuns(maxAge: null, maxPerMapping: 2));
        Assert.Equal(0, _store.PruneRuns(maxAge: null, maxPerMapping: 2));
    }
}
