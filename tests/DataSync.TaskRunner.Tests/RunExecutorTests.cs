using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
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
        _logWriter = new LogWriter(_stateDatabase);

        var driverRegistry = new DriverRegistry();
        driverRegistry.Register(new MsSqlDriver());

        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore, _taskRunStore,
            new ChangeWatermarkStore(_stateDatabase), _runLockStore, _logWriter);
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

    [Fact]
    public async Task ExecuteAsync_WhenReplicationDoesNotExist_ReturnsConfigError()
    {
        var result = await _executor.ExecuteAsync("nonexistent", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTableMappingHasMultipleSources_ReturnsConfigError()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources =
            [
                new SourceTableRef { ConnectionName = "src1", Database = "App", Table = "Orders" },
                new SourceTableRef { ConnectionName = "src2", Database = "App", Table = "Orders" },
            ],
            Targets = [new TableRef { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);

        var result = await _executor.ExecuteAsync("crm-sync", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTaskAlreadyLocked_ReturnsAlreadyRunning()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);
        _runLockStore.TryAcquire("crm-sync", Guid.NewGuid());

        var result = await _executor.ExecuteAsync("crm-sync", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExitCode.AlreadyRunning, result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSourceConnectionUnreachable_ReturnsConnectivityErrorAndReleasesLock()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
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

        var result = await _executor.ExecuteAsync("crm-sync", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExitCode.ConnectivityError, result);
        Assert.False(_runLockStore.IsLocked("crm-sync"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenConfigError_NeverCreatesATaskRunRow()
    {
        // A config/validation failure is caught before StartRun is ever called — there's nothing
        // real to attribute a run row to for a replication that doesn't exist.
        var result = await _executor.ExecuteAsync("nonexistent", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExitCode.ConfigError, result);
        Assert.Empty(_taskRunStore.GetRunHistory("nonexistent"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenConnectivityFails_StillRecordsAFailedTaskRunRow()
    {
        SaveTask("crm-sync");
        _configRepository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        }, Author);
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
        var runId = Guid.NewGuid();

        var result = await _executor.ExecuteAsync("crm-sync", runId, CancellationToken.None);

        Assert.Equal(ExitCode.ConnectivityError, result);
        var run = _taskRunStore.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.NotNull(run.ErrorSummary);
    }
}
