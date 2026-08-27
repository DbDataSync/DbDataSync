using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Scripting;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.State;
using DataSync.TaskRunner;
using LibGit2Sharp;
using Xunit;

namespace DataSync.TaskRunner.Tests;

/// <summary>
/// Exercises RunExecutor against real DataSync.Core (git-backed config) and DataSync.State (SQLite)
/// infrastructure — everything except an actual database connection, so these don't need SQL Server
/// and aren't tagged Category=Integration. A true end-to-end run against real MSSQL is covered
/// separately (see DataSync.TaskRunner.Tests' Integration-tagged tests).
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
        _repoRoot = Directory.CreateTempSubdirectory("datasync-taskrunner-tests-").FullName;
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
            _configRepository, driverRegistry, secretStore, _taskRunStore,
            new ChangeWatermarkStore(_stateDatabase), _runLockStore, _workQueueStore, _logWriter,
            Scripting.ForTests(_configRepository, _repoRoot));
    }

    public void Dispose()
    {
        _logWriter.Dispose();
        Directory.Delete(_repoRoot, recursive: true);
    }

    private void SaveTask(string name, string readerKind = "MsSqlChangeTracking") =>
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
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
}
