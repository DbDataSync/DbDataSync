namespace DataSync.State.Tests;

public sealed class TaskRunStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;
    private readonly TaskRunStore _store;

    public TaskRunStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void StartRun_ThenCompleteRun_RoundTrips()
    {
        var runId = Guid.NewGuid();
        _store.StartRun(runId, "crm-sync", pid: 4242);

        var started = _store.GetRun(runId);
        Assert.NotNull(started);
        Assert.Equal(RunStatus.Running, started!.Status);
        Assert.Equal(4242, started.Pid);
        Assert.Null(started.EndedAtUtc);

        _store.CompleteRun(runId, RunStatus.Succeeded, rowsRead: 100, rowsWritten: 98, errorSummary: null);

        var completed = _store.GetRun(runId);
        Assert.NotNull(completed);
        Assert.Equal(RunStatus.Succeeded, completed!.Status);
        Assert.Equal(100, completed.RowsRead);
        Assert.Equal(98, completed.RowsWritten);
        Assert.NotNull(completed.EndedAtUtc);
        Assert.Null(completed.ErrorSummary);
    }

    [Fact]
    public void CompleteRun_WithError_RecordsErrorSummary()
    {
        var runId = Guid.NewGuid();
        _store.StartRun(runId, "crm-sync", pid: null);
        _store.CompleteRun(runId, RunStatus.Failed, rowsRead: 5, rowsWritten: 0, errorSummary: "connection timed out");

        var run = _store.GetRun(runId);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Equal("connection timed out", run.ErrorSummary);
    }

    [Fact]
    public void GetRunHistory_ReturnsNewestFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _store.StartRun(first, "crm-sync", pid: 1);
        Thread.Sleep(5); // ensure StartedAtUtc ordering is unambiguous
        _store.StartRun(second, "crm-sync", pid: 2);

        var history = _store.GetRunHistory("crm-sync");

        Assert.Equal(2, history.Count);
        Assert.Equal(second, history[0].RunId);
        Assert.Equal(first, history[1].RunId);
    }

    [Fact]
    public void GetRunningRuns_OnlyReturnsRunsStillInProgress()
    {
        var running = Guid.NewGuid();
        var finished = Guid.NewGuid();
        _store.StartRun(running, "crm-sync", pid: 1);
        _store.StartRun(finished, "crm-sync", pid: 2);
        _store.CompleteRun(finished, RunStatus.Succeeded, 1, 1, null);

        var stillRunning = _store.GetRunningRuns();

        Assert.Single(stillRunning);
        Assert.Equal(running, stillRunning[0].RunId);
    }

    [Fact]
    public async Task ParallelRunWrites_AllRunsPersistedUnderContention()
    {
        const int count = 50;
        var runIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(runIds.Select(runId => Task.Run(() =>
        {
            _store.StartRun(runId, "crm-sync", pid: 1234);
            _store.CompleteRun(runId, RunStatus.Succeeded, rowsRead: 10, rowsWritten: 10, errorSummary: null);
        })));

        var history = _store.GetRunHistory("crm-sync", limit: count + 10);
        Assert.Equal(count, history.Count);
        Assert.All(history, r => Assert.Equal(RunStatus.Succeeded, r.Status));
    }
}
