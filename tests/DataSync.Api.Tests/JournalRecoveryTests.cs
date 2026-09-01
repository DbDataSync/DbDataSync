using DataSync.Api.Configuration;
using DataSync.Api.State;
using DataSync.State;
using DataSync.State.Remote;
using Microsoft.Extensions.Logging;

namespace DataSync.Api.Tests;

/// <summary>
/// What the owner does with what a runner left on disk. Recovery is the unusual path by construction,
/// so as much is asserted about what it *says* as about what it applies.
/// </summary>
public sealed class JournalRecoveryTests : IDisposable
{
    private const string TaskName = "sales";
    private const string MappingName = "orders";

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-journal-recovery-").FullName;
    private readonly StateDatabase _database;
    private readonly TaskRunStore _taskRuns;
    private readonly WorkQueueStore _workQueue;
    private readonly LogWriter _logs;
    private readonly LocalRunnerState _state;
    private readonly CapturingLogger _logger = new();
    private readonly JournalRecovery _recovery;

    public JournalRecoveryTests()
    {
        var stateDbPath = Path.Combine(_root, "state.db");
        _database = new StateDatabase(stateDbPath);
        _taskRuns = new TaskRunStore(_database);
        _workQueue = new WorkQueueStore(_database);
        _logs = new LogWriter(_database);
        _state = new LocalRunnerState(
            _taskRuns, _workQueue, new RunLockStore(_database), new ChangeWatermarkStore(_database),
            new VerificationResultStore(_database), _logs);

        _recovery = new JournalRecovery(_state, _taskRuns, _logs, new ApiOptions
        {
            RepoRoot = _root,
            StateDbPath = stateDbPath,
            TaskRunnerDllPath = "unused",
        }, _logger);
    }

    public void Dispose()
    {
        _logs.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string StateDbPath => Path.Combine(_root, "state.db");

    private string WriteJournal(Guid runId, params (JournalOperation Operation, object Payload)[] entries)
    {
        var path = StateJournal.PathFor(StateDbPath, TaskName, runId);
        using var journal = new StateJournal(path);
        foreach (var (operation, payload) in entries)
            journal.Append(operation, payload);
        return path;
    }

    [Fact]
    public void Nothing_to_recover_is_silent()
    {
        _recovery.Recover(TaskName);

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void Journalled_outcomes_are_applied_the_file_is_removed_and_it_says_so_loudly()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");
        var item = _workQueue.TryClaimNext(TaskName, "worker-1")!;

        var path = WriteJournal(runId,
            (JournalOperation.Log, new LogRequest(runId, LogSeverity.Info, "wrote 7 rows", DateTimeOffset.UtcNow)),
            (JournalOperation.SetWatermark, new SetWatermarkRequest(TaskName, "dbo.Orders", "1234", MappingName)),
            (JournalOperation.MarkDone, new WorkItemRequest(item.Id)),
            (JournalOperation.CompleteRun, new CompleteRunRequest(runId, RunStatus.Succeeded, 7, 7, null)));

        _recovery.Recover(TaskName);

        Assert.Equal("1234", _state.GetWatermark(TaskName, MappingName, "dbo.Orders"));
        Assert.Equal(RunStatus.Succeeded, _taskRuns.GetRun(runId)!.Status);
        Assert.Contains(_logs.GetLogs(runId), l => l.Message == "wrote 7 rows");
        Assert.False(_workQueue.HasOutstandingWork(TaskName));
        Assert.False(File.Exists(path));

        // A normal installation never produces one of these; an operator should be able to find out
        // that it happened.
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("recovered from disk"));
    }

    /// <summary>
    /// The window this covers is a process that dies between applying a journal and deleting it. Every
    /// operation is last-writer-wins except appending a log line, which is why that one carries a key.
    /// </summary>
    [Fact]
    public void Applying_the_same_journal_twice_leaves_one_outcome_and_one_of_each_log_line()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");
        var item = _workQueue.TryClaimNext(TaskName, "worker-1")!;

        (JournalOperation, object)[] entries =
        [
            (JournalOperation.Log, new LogRequest(runId, LogSeverity.Info, "wrote 7 rows", DateTimeOffset.UtcNow)),
            (JournalOperation.MarkDone, new WorkItemRequest(item.Id)),
            (JournalOperation.CompleteRun, new CompleteRunRequest(runId, RunStatus.Succeeded, 7, 7, null)),
        ];

        WriteJournal(runId, entries);
        _recovery.Recover(TaskName);
        WriteJournal(runId, entries);
        _recovery.Recover(TaskName);

        Assert.Single(_logs.GetLogs(runId), l => l.Message == "wrote 7 rows");
        var run = _taskRuns.GetRun(runId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(7, run.RowsRead);
    }

    /// <summary>
    /// Both observations are honest and they disagree: this process watched a process exit, the runner
    /// watched whether the rows landed. The runner knew more, so the journal wins — and the correction
    /// is logged, because an outcome changing after the fact is exactly what someone will come looking
    /// for later.
    /// </summary>
    [Fact]
    public void The_journal_wins_a_conflict_and_the_correction_is_logged()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");
        _taskRuns.CompleteRun(runId, RunStatus.Failed, 0, 0, "the runner exited with code 5");

        WriteJournal(runId, (JournalOperation.CompleteRun, new CompleteRunRequest(runId, RunStatus.Succeeded, 7, 7, null)));
        _recovery.Recover(TaskName);

        Assert.Equal(RunStatus.Succeeded, _taskRuns.GetRun(runId)!.Status);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Correcting run") && e.Message.Contains("Failed"));
    }

    /// <summary>
    /// A runner that lost its owner mid-item spills a release, never a completion — so the item is
    /// picked up again rather than silently dropped.
    /// </summary>
    [Fact]
    public void A_claimed_but_unfinished_item_comes_back_claimable()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");
        var item = _workQueue.TryClaimNext(TaskName, "worker-1")!;
        Assert.Null(_workQueue.TryClaimNext(TaskName, "worker-2"));

        WriteJournal(runId,
            (JournalOperation.ReleaseClaim, new WorkItemRequest(item.Id)),
            (JournalOperation.ReleaseLock, new ReleaseLockRequest(TaskName, RunKind.Primary, "Orders")));

        _recovery.Recover(TaskName);

        var reclaimed = _workQueue.TryClaimNext(TaskName, "worker-2");
        Assert.NotNull(reclaimed);
        Assert.Equal(item.Id, reclaimed.Id);
    }

    [Fact]
    public void A_truncated_journal_is_applied_as_far_as_it_goes_and_the_loss_is_reported()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");

        var path = WriteJournal(runId,
            (JournalOperation.SetWatermark, new SetWatermarkRequest(TaskName, "dbo.Orders", "1234", MappingName)));
        File.AppendAllText(path, """{"sequence":2,"operation":"Comp""");

        _recovery.Recover(TaskName);

        Assert.Equal("1234", _state.GetWatermark(TaskName, MappingName, "dbo.Orders"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("unreadable line"));
    }

    private sealed class CapturingLogger : ILogger<JournalRecovery>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
