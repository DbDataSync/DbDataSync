using DbDataSync.Api.Configuration;
using DbDataSync.Api.State;
using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.State.Remote;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Api.Tests;

/// <summary>
/// What the owner does with what a runner left on disk. Recovery is the unusual path by construction,
/// so as much is asserted about what it *says* as about what it applies.
/// </summary>
public sealed class JournalRecoveryTests : IDisposable
{
    private const string TaskName = "sales";
    private const string MappingName = "orders";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-journal-recovery-").FullName;
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

    /// <summary>
    /// A runner that journalled its watermark offline journalled the source's own time for that
    /// position with it, and replay must land both — a position recovered without its time would
    /// report "no lag data" for a mapping that had in fact measured itself perfectly well, until its
    /// next pass happened to overwrite the row. See phase 87.
    /// </summary>
    [Fact]
    public void A_journalled_watermark_carries_its_cached_commit_time_through_replay()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");
        var committed = new DateTimeOffset(2026, 3, 1, 11, 55, 0, TimeSpan.Zero);

        WriteJournal(runId, (JournalOperation.SetWatermark,
            new SetWatermarkRequest(TaskName, "dbo.Orders", "1234", MappingName, committed)));

        _recovery.Recover(TaskName);

        var applied = new ChangeWatermarkStore(_database)
            .GetAppliedPosition(TaskName, MappingName, "dbo.Orders");

        Assert.Equal("1234", applied!.Watermark);
        Assert.Equal(committed, applied.WatermarkTimeUtc);
    }

    /// <summary>
    /// **An entry from a runner older than phase 87 replays, and replays as a watermark.** The
    /// position is the outcome the journal exists to preserve; the commit time is a convenience for a
    /// status screen, and its absence is a state lag reporting already answers for. Refusing the
    /// entry — the treatment the missing *mapping name* gets, one field along — would trade a real
    /// replication result for a missing number.
    /// </summary>
    [Fact]
    public void A_journalled_watermark_from_before_the_cache_existed_still_replays()
    {
        _state.UpsertTask(TaskName, enabled: true);
        var runId = _workQueue.Enqueue(TaskName, RunKind.Primary, "Orders");

        WriteJournal(runId, (JournalOperation.SetWatermark,
            new SetWatermarkRequest(TaskName, "dbo.Orders", "1234", MappingName)));

        _recovery.Recover(TaskName);

        var applied = new ChangeWatermarkStore(_database)
            .GetAppliedPosition(TaskName, MappingName, "dbo.Orders");

        Assert.Equal("1234", applied!.Watermark);
        Assert.Null(applied.WatermarkTimeUtc);
    }

    // ---- Read intent and hold (phase 100) ----------------------------------------------------

    /// <summary>The operations exist in the journal from phase 100 on even though nothing writes them
    /// yet, so a cross-instance deployment does not silently drop them the day phase 101 does.</summary>
    [Fact]
    public void Journalled_read_intent_and_hold_are_applied()
    {
        _state.UpsertTask(TaskName, enabled: true);

        WriteJournal(Guid.NewGuid(),
            (JournalOperation.SetReadIntent,
                new SetReadIntentRequest(TaskName, "dbo.Orders", ReadIntent.ChangesFromEarliest, MappingName)),
            (JournalOperation.SetReadHold,
                new SetReadHoldRequest(TaskName, "dbo.Orders", ReadHold.PositionExpired, MappingName)));

        _recovery.Recover(TaskName);

        var state = new ChangeWatermarkStore(_database).GetReadState(TaskName, MappingName, "dbo.Orders");
        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.PositionExpired, state.Hold);
    }

    /// <summary>Both are idempotent by construction — the payload names the value the row should end up
    /// at, not a transition — so replaying the same journal twice must not, say, toggle a hold back off.</summary>
    [Fact]
    public void Replaying_read_intent_and_hold_twice_leaves_the_same_value_the_first_replay_did()
    {
        _state.UpsertTask(TaskName, enabled: true);
        (JournalOperation, object)[] entries =
        [
            (JournalOperation.SetReadIntent,
                new SetReadIntentRequest(TaskName, "dbo.Orders", ReadIntent.ChangesFromLatest, MappingName)),
            (JournalOperation.SetReadHold,
                new SetReadHoldRequest(TaskName, "dbo.Orders", ReadHold.Paused, MappingName)),
        ];

        WriteJournal(Guid.NewGuid(), entries);
        _recovery.Recover(TaskName);
        WriteJournal(Guid.NewGuid(), entries);
        _recovery.Recover(TaskName);

        var state = new ChangeWatermarkStore(_database).GetReadState(TaskName, MappingName, "dbo.Orders");
        Assert.Equal(ReadIntent.ChangesFromLatest, state!.Intent);
        Assert.Equal(ReadHold.Paused, state.Hold);
    }

    /// <summary>
    /// Neither of these has a phase-74-style ambiguity to worry about — they were born with a mapping
    /// name — but a request without one is refused the same way <c>set-watermark</c> refuses it, and
    /// recovery has to cope with that shape existing in principle.
    /// </summary>
    [Fact]
    public void A_read_intent_journalled_with_no_mapping_is_skipped_rather_than_guessed()
    {
        _state.UpsertTask(TaskName, enabled: true);

        WriteJournal(Guid.NewGuid(), (JournalOperation.SetReadIntent,
            new SetReadIntentRequest(TaskName, "dbo.Orders", ReadIntent.ChangesFromEarliest)));

        _recovery.Recover(TaskName);

        Assert.Null(new ChangeWatermarkStore(_database).GetReadState(TaskName, MappingName, "dbo.Orders"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("names no mapping"));
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
