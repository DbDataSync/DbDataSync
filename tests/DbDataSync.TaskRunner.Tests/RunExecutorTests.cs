using System.Diagnostics;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
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
                new ChangeWatermarkStore(_stateDatabase), new VerificationResultStore(_stateDatabase), _logWriter,
                new BulkLoadBatchStore(_stateDatabase), new Lazy<IInitialLoadEnqueuer>(() => new NeverCalledInitialLoadEnqueuer())),
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
        await _executor.ExecuteWorkerAsync(taskName, WorkerLanes.Uniform(1), CancellationToken.None);
        return runId;
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenReplicationDoesNotExist_ReturnsConfigError()
    {
        var result = await _executor.ExecuteWorkerAsync("nonexistent", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
    }

    [Fact]
    public async Task ExecuteWorkerAsync_WhenConfigError_NeverCreatesATaskRunRow()
    {
        // A config-load failure happens before any WorkQueue item could exist for this task name —
        // there's nothing real to attribute a run row to for a replication that doesn't exist.
        var result = await _executor.ExecuteWorkerAsync("nonexistent", WorkerLanes.Uniform(1), CancellationToken.None);

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
            DriverType = DriverIds.MsSql,
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
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var ordersRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "orders");
        var customersRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "customers");
        Assert.NotEqual(ordersRunId, customersRunId);

        await _executor.ExecuteWorkerAsync("crm-sync", WorkerLanes.Uniform(2), CancellationToken.None);

        var ordersRun = _taskRunStore.GetRun(ordersRunId);
        var customersRun = _taskRunStore.GetRun(customersRunId);
        Assert.Equal("orders", ordersRun!.MappingName);
        Assert.Equal("customers", customersRun!.MappingName);
        Assert.False(_runLockStore.IsLocked("crm-sync", RunKind.Primary, "orders"));
        Assert.False(_runLockStore.IsLocked("crm-sync", RunKind.Primary, "customers"));
    }

    /// <summary>
    /// Originally phase 100's own seam test (storage exists, nothing consumes it yet); phase 101 is the
    /// change that made its old premise stop being true on purpose, so this is retargeted to what
    /// remains true after that change: a **supported, declared** intent stored ahead of a mapping's
    /// first pass does not make <c>RunExecutor</c> behave any differently from a mapping with nothing
    /// stored at all — both reach the same reader, are refused by neither (Change Tracking declares
    /// <c>ChangesFromEarliest</c>; <c>InitialLoad</c> is never refused), and fail identically once they
    /// both reach the same unreachable connection. Refusal of an *undeclared* intent, and the
    /// <c>InitialLoad</c> exemption specifically, are their own tests below.
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_AStoredSupportedReadIntent_FailsIdenticallyToNoStoredIntent()
    {
        SaveTask("crm-sync");
        foreach (var name in new[] { "with-intent", "without-intent" })
        {
            _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
            {
                Name = name,
                Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
                Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
            }, Author);
        }

        // Both mappings share this same unreachable source, so any difference between their runs can
        // only come from the stored intent — never from the connection.
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var watermarks = new ChangeWatermarkStore(_stateDatabase);
        var watermarkKey = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" },
            MsSqlDialect.Instance);

        // Set ahead of the run, with no watermark behind it — exactly the "brand-new mapping under a
        // configured ChangesFromEarliest default" case the plan doc calls out as the one real decision
        // this whole design makes about data. Phase 100 says storing this changes nothing yet.
        watermarks.SetReadIntent("crm-sync", "with-intent", watermarkKey, ReadIntent.ChangesFromEarliest);

        var withIntentRunId = await EnqueueAndDrainAsync("crm-sync", "with-intent");
        var withoutIntentRunId = await EnqueueAndDrainAsync("crm-sync", "without-intent");

        var withIntentRun = _taskRunStore.GetRun(withIntentRunId)!;
        var withoutIntentRun = _taskRunStore.GetRun(withoutIntentRunId)!;

        Assert.Equal(RunStatus.Failed, withIntentRun.Status);
        Assert.Equal(withoutIntentRun.Status, withIntentRun.Status);
        Assert.Equal(withoutIntentRun.FailureKind, withIntentRun.FailureKind);
        Assert.NotNull(withIntentRun.ErrorSummary);
        Assert.NotNull(withoutIntentRun.ErrorSummary);

        // Both failures are the connection failing, not a refusal — proof the stored intent was
        // accepted rather than silently downgraded or bounced before RunExecutor even tried the source.
        Assert.Contains("Failed to open connection", withIntentRun.ErrorSummary);
        Assert.Contains("Failed to open connection", withoutIntentRun.ErrorSummary);

        // Neither pass ever opened the connection, so neither wrote a watermark — the stored intent did
        // not make this pass behave as though it had read something.
        Assert.Null(watermarks.GetWatermark("crm-sync", "with-intent", watermarkKey));
        Assert.Null(watermarks.GetWatermark("crm-sync", "without-intent", watermarkKey));

        // And the intent itself is exactly what was set before the run: nothing read it, let alone
        // consumed or transitioned it — the "nothing reads the intent when this phase ends" guarantee,
        // checked rather than assumed.
        Assert.Equal(
            ReadIntent.ChangesFromEarliest,
            watermarks.GetReadState("crm-sync", "with-intent", watermarkKey)!.Intent);
    }

    /// <summary>
    /// The guarantee phase 101 exists to give: an intent the reader has not declared is refused loudly
    /// and **before any connection is opened** — never silently downgraded, and never discovered only
    /// once the source has already been asked something it cannot answer. The Watermark reader
    /// deliberately does not declare <see cref="ReadIntent.ChangesFromEarliest"/> (for it the feed *is*
    /// the table, so offering one would be a button that lies) — exactly the undeclared case this test
    /// needs.
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_AnUndeclaredIntent_IsRefused_BeforeAnyConnectionIsOpened()
    {
        SaveTask("crm-sync", readerKind: "Watermark");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        // Unreachable — if the refusal did not happen first, this connection attempt is what would
        // fail instead, and the two failures read very differently (see the assertions below).
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var watermarks = new ChangeWatermarkStore(_stateDatabase);
        var watermarkKey = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" },
            MsSqlDialect.Instance);
        watermarks.SetReadIntent("crm-sync", "orders", watermarkKey, ReadIntent.ChangesFromEarliest);

        var runId = await EnqueueAndDrainAsync("crm-sync", "orders");

        var run = _taskRunStore.GetRun(runId)!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("does not support", run.ErrorSummary);
        Assert.Contains("ChangesFromEarliest", run.ErrorSummary);
        // Not a connectivity failure — the whole point is that this never got as far as trying.
        Assert.DoesNotContain("Failed to open connection", run.ErrorSummary);
    }

    /// <summary>
    /// The retarget's own guarantee: <see cref="ReadIntent.InitialLoad"/> is never refused, regardless
    /// of what a reader declares — the Watermark reader here declares only <c>Changes</c> and
    /// <c>ChangesFromLatest</c> (see <see cref="DbDataSync.Drivers.Generic.WatermarkReader.SupportedIntents"/>),
    /// so a refusal check that did not exempt <c>InitialLoad</c> would refuse it too. Instead this run
    /// gets past the check and fails on the unreachable connection — proof the exemption is real,
    /// not merely that this reader happens to support it.
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_InitialLoad_IsNeverRefused_EvenWhenUndeclared()
    {
        SaveTask("crm-sync", readerKind: "Watermark");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        // No stored read state at all: resolves to ReadIntentResolution.Default, which is InitialLoad
        // for a mapping and replication that set no override.

        var runId = await EnqueueAndDrainAsync("crm-sync", "orders");

        var run = _taskRunStore.GetRun(runId)!;
        Assert.Equal(RunStatus.Failed, run.Status);
        // Reached the connection attempt rather than being bounced by the refusal check.
        Assert.Contains("Failed to open connection", run.ErrorSummary);
        Assert.DoesNotContain("does not support", run.ErrorSummary);
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
        await _executor.ExecuteWorkerAsync("resident", WorkerLanes.Uniform(1), CancellationToken.None);
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
        var worker = _executor.ExecuteWorkerAsync("responsive", WorkerLanes.Uniform(1), cancellation.Token);

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
    /// One worker, two lanes: a Primary pass and a BulkLoad segment queued together are both claimed
    /// and both driven to a terminal state in a single invocation. (Both fail for want of a database,
    /// which is beside the point — they were each picked up, in their own lane.)
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_DrainsBothLanes()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        var primaryRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "orders");
        var bulkLoadRunId = _workQueueStore.Enqueue(
            "crm-sync", RunKind.BulkLoad, "orders", segmentLabel: "full", segmentJson: "{\"mode\":\"full\"}");

        await _executor.ExecuteWorkerAsync("crm-sync", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, _taskRunStore.GetRun(primaryRunId)!.Status);
        Assert.Equal(RunStatus.Failed, _taskRunStore.GetRun(bulkLoadRunId)!.Status);
    }

    /// <summary>
    /// The point of the split: a bulk load segment already Running — the "long reload holding a slot"
    /// case — does not keep a Primary pass from being claimed and finished, and does not keep the
    /// worker alive past its idle timeout either (the bulk load lane winds down with the change lane
    /// even while a Running row it does not own is still on the books).
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_ABusyBulkLoadLane_DoesNotBlockChangeProcessing()
    {
        SaveTask("crm-sync"); // continuous, 2s idle timeout
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "127.0.0.1",
            Port = 1,
            Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
            Properties = new Dictionary<string, string> { ["Connect Timeout"] = "1" },
        }, Author);

        // Pin a bulk load segment Running and never finish it — a stand-in for a reload that takes
        // hours. Claimed by a different "worker", so the worker under test never touches it.
        _workQueueStore.Enqueue("crm-sync", RunKind.BulkLoad, "orders", segmentLabel: "seg-1");
        var pinned = _workQueueStore.TryClaimNext("crm-sync", "slow-bulk-load", RunLane.BulkLoad)!;
        _workQueueStore.MarkRunning(pinned.Id);

        var primaryRunId = _workQueueStore.Enqueue("crm-sync", RunKind.Primary, "orders");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _executor.ExecuteWorkerAsync(
            "crm-sync", new WorkerLanes(ChangeProcessing: 1, BulkLoad: 1), cancellation.Token);

        Assert.False(cancellation.IsCancellationRequested, "the worker did not exit on its own — the bulk load lane hung.");
        Assert.Equal(RunStatus.Failed, _taskRunStore.GetRun(primaryRunId)!.Status);
        // The worker under test never claimed the pinned segment — it belongs to another worker — so
        // it recorded no outcome for it.
        Assert.NotEqual(RunStatus.Failed, _taskRunStore.GetRun(pinned.RunId)!.Status);
    }

    /// <summary>
    /// A regression test for a bug phase 133 shipped: a BulkLoad item with no explicit
    /// <see cref="WorkItemKinds"/> override must resolve its reader/cache/writer against
    /// <see cref="ReplicationTaskConfig.BulkLoad"/>, not <see cref="ReplicationTaskConfig.ChangeProcessing"/>
    /// — the whole point of giving Bulk Load its own pipeline. Proven here by configuring
    /// <c>BulkLoad.Reader</c> with a Kind the registered driver does not offer at all: if resolution ever
    /// regresses back to <c>ChangeProcessing.Reader</c> (a Kind the driver *does* offer,
    /// <c>MsSqlChangeTracking</c>), this fails to fail — it would instead proceed to (and fail at) opening
    /// a connection, not at the reader-kind check itself.
    /// </summary>
    [Fact]
    public async Task ExecuteWorkerAsync_ABulkLoadWithNoOverride_ResolvesAgainstBulkLoadConfig_NotChangeProcessing()
    {
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "crm-sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            BulkLoad = new BulkLoadConfig { Reader = new ReaderConfig { Kind = "NoSuchReaderKind" } },
        }, Author);
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);
        _configRepository.SaveConnection(new ConnectionInput
        {
            Name = "src", DriverType = DriverIds.MsSql, Host = "127.0.0.1", Port = 1, Database = "App",
            AuthMode = AuthMode.IntegratedAuth,
        }, Author);

        var bulkLoadRunId = _workQueueStore.Enqueue(
            "crm-sync", RunKind.BulkLoad, "orders", segmentLabel: "full", segmentJson: "{\"mode\":\"full\"}");

        await _executor.ExecuteWorkerAsync("crm-sync", WorkerLanes.Uniform(1), CancellationToken.None);

        var run = _taskRunStore.GetRun(bulkLoadRunId)!;
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("does not support reader kind 'NoSuchReaderKind'", run.ErrorSummary);
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
        await _executor.ExecuteWorkerAsync("nightly", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "a cron worker waited out an idle timeout.");
    }

    #endregion
}
