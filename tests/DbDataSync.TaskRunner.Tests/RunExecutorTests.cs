using System.Diagnostics;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Scripting;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using DbDataSync.TaskRunner;
using LibGit2Sharp;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Exercises RunExecutor against real DbDataSync.Core (git-backed config) and DbDataSync.State (SQLite)
/// infrastructure — everything except an actual database connection, so these don't need SQL Server
/// and aren't tagged Category=Integration. A true end-to-end run against real MSSQL is covered
/// separately (see DbDataSync.TaskRunner.Tests' Integration-tagged tests).
/// </summary>
public sealed class RunExecutorTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _repoRoot;
    private readonly ConfigRepository _configRepository;
    private readonly StateDatabase _stateDatabase;
    private readonly TaskRunStore _taskRunStore;
    private readonly RunLockStore _runLockStore;
    private readonly WorkQueueStore _workQueueStore;
    private readonly LogWriter _logWriter;
    private readonly RunExecutor _executor;

    public RunExecutorTests()
    {
        _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-taskrunner-tests-").FullName;
        Repository.Init(_repoRoot);

        var secretStore = SecretStore.ForProviders([new InMemorySecretProvider()]);
        _configRepository = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secretStore);
        _stateDatabase = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        _taskRunStore = new TaskRunStore(_stateDatabase);
        _runLockStore = new RunLockStore(_stateDatabase);
        _workQueueStore = new WorkQueueStore(_stateDatabase);
        _logWriter = new LogWriter(_stateDatabase);

        var driverRegistry = new DriverRegistry();
        driverRegistry.Register(new MsSqlDriver());

        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore,
            new LocalRunnerState(_taskRunStore, _workQueueStore, _runLockStore,
                new ChangeWatermarkStore(_stateDatabase), new VerificationResultStore(_stateDatabase), _logWriter),
            new LocalRunnerConfig(_configRepository, Author),
            Scripting.ForTests(_configRepository, _repoRoot),
            Path.Combine(_repoRoot, "state.db"));
    }

    public void Dispose()
    {
        _logWriter.Dispose();
        Directory.Delete(_repoRoot, recursive: true);
    }

    private void SaveTask(
        string name, ScheduleMode mode, int frequencySeconds, int? idleTimeoutSeconds) =>
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig
            {
                Mode = mode,
                FrequencySeconds = mode == ScheduleMode.Continuous ? frequencySeconds : null,
                CronExpression = mode == ScheduleMode.Periodic ? "0 0 * * *" : null,
                IdleTimeoutSeconds = idleTimeoutSeconds,
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);

    private void SaveTask(string name, string readerKind = "MsSqlChangeTracking") =>
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous,
                // A continuous worker now stays resident until it has gone a whole idle timeout
                // without a pass reading anything. These tests drain a queue and want the worker to
                // leave promptly, so they say so — the production default is 60 seconds.
                FrequencySeconds = 1,
                IdleTimeoutSeconds = 2,
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = readerKind },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);

    /// <summary>Enqueues a Primary pass for one mapping and drains the queue with a single-consumer
    /// worker — the smallest unit that exercises the real claim -> lock -> run -> complete pipeline.</summary>
    private async Task<Guid> EnqueueAndDrainAsync(string taskName, string mappingName)
    {
        var runId = _workQueueStore.Enqueue(taskName, RunKind.Primary, mappingName);
        await _executor.ExecuteWorkerAsync(taskName, degreeOfParallelism: 1, CancellationToken.None);
        return runId;
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenReplicationDoesNotExist_ReturnsConfigError()
    {
        var result = await _executor.ExecuteWorkerAsync("nonexistent", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenConfigError_NeverCreatesATaskRunRow()
    {
        // A config-load failure happens before any WorkQueue item could exist for this task name —
        // there's nothing real to attribute a run row to for a replication that doesn't exist.
        var result = await _executor.ExecuteWorkerAsync("nonexistent", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
        Assert.Empty(_taskRunStore.GetRunHistory("nonexistent"));
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenTableMappingHasMultipleSources_FailsThatMappingsRunOnly()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources =
            [
                new SourceTableSpec { ConnectionName = "src1", Database = "App", Table = "Orders" },
                new SourceTableSpec { ConnectionName = "src2", Database = "App", Table = "Orders" },
            ],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        var runId = await EnqueueAndDrainAsync("crm-sync", "orders");

        // The worker itself started and drained cleanly — per-item outcomes live in TaskRuns, not the
        // overall exit code, once work is queue-driven rather than one shared run per invocation.
        var run = _taskRunStore.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Contains("1:1 mappings", run.ErrorSummary);
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenSourceConnectionUnreachable_RecordsFailureAndReleasesLock()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        // Nothing listens on 127.0.0.1:1 — SqlClient should fail fast (Connect Timeout=1s).
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var runId = await EnqueueAndDrainAsync("crm-sync", "orders");

        var run = _taskRunStore.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.NotNull(run.ErrorSummary);
        Assert.False(_runLockStore.IsLocked("crm-sync", RunKind.Primary, "orders"));
    }

    [Fact]
    public async Task ExecuteWorkerAsync_MultipleMappings_EachGetsItsOwnRunIdAndLock()
    {
        SaveTask("crm-sync");
        foreach (var name in new[] { "orders", "customers" })
        {
            _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
            {
                Name = name,
                Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = name }],
                Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = name }],
            }, Author);
        }
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = ConnectionDriverType.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var ordersRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "orders");
        var customersRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "customers");
        Assert.NotEqual(ordersRunId, customersRunId);

        await _executor.ExecuteWorkerAsync("crm-sync", degreeOfParallelism: 2, CancellationToken.None);

        var ordersRun = _taskRunStore.GetRun(ordersRunId);
        var customersRun = _taskRunStore.GetRun(customersRunId);
        Assert.Equal("orders", ordersRun!.MappingName);
        Assert.Equal("customers", customersRun!.MappingName);
        Assert.False(_runLockStore.IsLocked("crm-sync", RunKind.Primary, "orders"));
        Assert.False(_runLockStore.IsLocked("crm-sync", RunKind.Primary, "customers"));
    }

    #region The continuous worker's idle timeout

    /// <summary>
    /// The bug this exists for: the worker exited as soon as the *queue* was empty, which is true for
    /// a fraction of a second after every single pass. Under a live load that meant spawn, one pass,
    /// exit, respawn, several times a minute — process churn larger than the work.
    /// </summary>
    [Fact]
    public async Task AContinuousWorker_StaysUpForTheIdleTimeoutRatherThanLeavingOnAnEmptyQueue()
    {
        // Comfortably longer than the five one-second empty polls the old exit rule used, so this
        // fails against that behaviour rather than passing by coincidence.
        SaveTask("resident", ScheduleMode.Continuous, frequencySeconds: 1, idleTimeoutSeconds: 8);

        var started = Stopwatch.GetTimestamp();
        await _executor.ExecuteWorkerAsync("resident", degreeOfParallelism: 1, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed >= TimeSpan.FromSeconds(6.5), $"left after {elapsed.TotalSeconds:F1}s.");
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"outstayed its timeout by a long way ({elapsed.TotalSeconds:F1}s).");
    }

    /// <summary>
    /// The wait between passes is the frequency, but it is not a sleep: a person pressing Run Now
    /// no-ops <c>EnsureWorkerRunning</c> while this process is alive, so if the worker slept through
    /// the interval, so would they.
    /// </summary>
    [Fact]
    public async Task AWaitingWorker_ClaimsWorkEnqueuedDuringTheInterval()
    {
        SaveTask("responsive", ScheduleMode.Continuous, frequencySeconds: 30, idleTimeoutSeconds: 60);
        _configRepository.SaveTableMapping("responsive", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        using var cancellation = new CancellationTokenSource();
        var worker = _executor.ExecuteWorkerAsync("responsive", degreeOfParallelism: 1, cancellation.Token);

        await Task.Delay(TimeSpan.FromSeconds(1));
        var runId = _workQueueStore.Enqueue("responsive", RunKind.Primary, "main");

        // Claimed and finished well inside the 30s interval it was enqueued into — the run fails for
        // want of a database, which is beside the point: it was *picked up*.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && _taskRunStore.GetRun(runId)?.Status is null or RunStatus.Queued)
            await Task.Delay(100);

        Assert.NotEqual(RunStatus.Queued, _taskRunStore.GetRun(runId)!.Status);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>
    /// A cron replication keeps the old fast exit. Its next occurrence can be hours away, and holding
    /// a process open for that is not restraint, it is a leak.
    /// </summary>
    [Fact]
    public async Task APeriodicWorker_StillLeavesAsSoonAsTheQueueIsEmpty()
    {
        SaveTask("nightly", ScheduleMode.Periodic, frequencySeconds: 0, idleTimeoutSeconds: null);

        var started = Stopwatch.GetTimestamp();
        await _executor.ExecuteWorkerAsync("nightly", degreeOfParallelism: 1, CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "a cron worker waited out an idle timeout.");
    }

    #endregion
}
