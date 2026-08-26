using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.State;
using DataSync.TaskRunner;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.TaskRunner.Tests;

/// <summary>
/// The one true end-to-end test of the v1 vertical slice: real git-backed config (Phase 1), real
/// SQLite state (Phase 2), the real MSSQL driver (Phase 3), and RunExecutor (Phase 4) all wired
/// together and pointed at a real SQL Server, exactly as a hand invocation of the compiled
/// DataSync.TaskRunner executable would be. Needs the same Docker SQL Server container as
/// DataSync.Drivers.MsSql.Tests — see that project's MsSqlTestDatabase for the connection string
/// convention (duplicated here rather than shared: this is only the second consumer of that fixture
/// shape, and it's ~30 lines — see architecture/implementation/done/phase-003-mssql-driver.md's notes on
/// when to extract a shared test-support project instead).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunExecutorIntegrationTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly string _databaseName = $"DataSyncTaskRunnerTest_{Guid.NewGuid():N}";
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("datasync-taskrunner-e2e-").FullName;
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _reloadTargetTable = $"ReloadTgt_{Guid.NewGuid():N}";

    private ConfigRepository _configRepository = null!;
    private RunExecutor _executor = null!;
    private WorkQueueStore _workQueueStore = null!;
    private TaskRunStore _taskRunStore = null!;
    private ChangeWatermarkStore _watermarkStore = null!;
    private SqlConnection _adminConnection = null!;

    public async Task InitializeAsync()
    {
        Repository.Init(_repoRoot);

        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        var dbBuilder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        _adminConnection = new SqlConnection(dbBuilder.ConnectionString);
        await _adminConnection.OpenAsync();

        await ExecuteAsync(_adminConnection, $"""
            CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_adminConnection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(_adminConnection, $"""
            CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            """);
        await ExecuteAsync(_adminConnection, $"""
            CREATE TABLE dbo.[{_reloadTargetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            """);

        var secretStore = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var stateDatabase = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        var driverRegistry = new DriverRegistry();
        driverRegistry.Register(new MsSqlDriver());

        _configRepository = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secretStore);
        _taskRunStore = new TaskRunStore(stateDatabase);
        _workQueueStore = new WorkQueueStore(stateDatabase);
        _watermarkStore = new ChangeWatermarkStore(stateDatabase);
        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore, _taskRunStore,
            _watermarkStore, new RunLockStore(stateDatabase), _workQueueStore, new LogWriter(stateDatabase));

        SetUpConfig();
    }

    public async Task DisposeAsync()
    {
        _adminConnection.Dispose();

        await using var bootstrap = new SqlConnection(ServerConnectionString);
        await bootstrap.OpenAsync();
        await ExecuteAsync(bootstrap, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(bootstrap, $"DROP DATABASE [{_databaseName}];");

        Directory.Delete(_repoRoot, recursive: true);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private void SetUpConfig()
    {
        ConnectionInput MakeConnectionInput(string name) => new()
        {
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
            Name = name,
        };
        _configRepository.SaveConnection(MakeConnectionInput("src-conn"), Author);
        _configRepository.SaveConnection(MakeConnectionInput("tgt-conn"), Author);

        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "e2e-sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);

        _configRepository.SaveTableMapping("e2e-sync", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableRef { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = _targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, Author);
    }

    private async Task<Dictionary<int, string>> GetTargetRowsAsync()
    {
        await using var cmd = _adminConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Name FROM dbo.[{_targetTable}];";
        var results = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results[reader.GetInt32(0)] = reader.GetString(1);
        return results;
    }

    /// <summary>Enqueues a Primary pass for the "main" mapping and drains it with a single-consumer
    /// worker, returning the final TaskRuns row for that pass.</summary>
    private async Task<TaskRunRecord> EnqueueAndDrainAsync()
    {
        var runId = _workQueueStore.Enqueue("e2e-sync", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("e2e-sync", degreeOfParallelism: 1, CancellationToken.None);
        return _taskRunStore.GetRun(runId)!;
    }

    [Fact]
    public async Task FullPipeline_FullLoadThenIncrementalRun_ReplicatesAndRecordsState()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var firstRun = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Succeeded, firstRun.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await GetTargetRowsAsync());

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (3, 'Carol');");
        await ExecuteAsync(_adminConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync(_adminConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 1;");

        var secondRun = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Succeeded, secondRun.Status);
        Assert.Equal(new Dictionary<int, string> { [2] = "Robert", [3] = "Carol" }, await GetTargetRowsAsync());
    }

    private async Task<Dictionary<int, string>> GetRowsAsync(string table)
    {
        await using var cmd = _adminConnection.CreateCommand();
        cmd.CommandText = $"SELECT Id, Name FROM dbo.[{table}];";
        var results = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results[reader.GetInt32(0)] = reader.GetString(1);
        return results;
    }

    private string WatermarkFor(string table) =>
        _watermarkStore.GetWatermark("e2e-sync", WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = table }))!;

    /// <summary>
    /// The guarantee the whole per-mapping run model exists for: a backfill re-reads and re-applies
    /// data the incremental sync has already processed, using a completely different reader and
    /// writer, and the incremental sync's watermark comes out of it untouched. If it didn't, running
    /// a backfill would silently make the replication re-process (or skip) changes afterwards.
    /// </summary>
    [Fact]
    public async Task Backfill_UsesItsOwnPipelineOverSegment_AndLeavesTheIncrementalWatermarkAlone()
    {
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One'), (2, 'Two'), (7, 'Seven');");
        await EnqueueAndDrainAsync();
        var watermarkAfterIncremental = WatermarkFor(_sourceTable);

        // Diverge the target from the source *behind* the incremental sync's back, the way a
        // mis-applied change or an out-of-band edit would.
        await ExecuteAsync(_adminConnection, $"UPDATE dbo.[{_targetTable}] SET Name = 'corrupted' WHERE Id = 1;");
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_targetTable}] (Id, Name) VALUES (3, 'never-existed');");

        // Reload Ids [1, 5) only, with a reload reader and a reconciling writer — neither of which is
        // what this replication is configured to use for its ongoing sync.
        var segment = new RangeSegment("Id", "1", "5");
        var backfillRunId = _workQueueStore.Enqueue(
            "e2e-sync", RunKind.Backfill, "main", segment.Describe(), SegmentSerializer.Serialize(segment),
            new WorkItemKinds(MsSqlDriverKinds.BatchReload, MsSqlDriverKinds.StagingTable, MsSqlDriverKinds.MergeReconcile));
        await _executor.ExecuteWorkerAsync("e2e-sync", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(backfillRunId)!.Status);

        var rows = await GetRowsAsync(_targetTable);
        Assert.Equal("One", rows[1]);                 // repaired
        Assert.False(rows.ContainsKey(3));            // in the segment, absent from the source — removed
        Assert.Equal("Seven", rows[7]);               // outside the segment — untouched

        Assert.Equal(watermarkAfterIncremental, WatermarkFor(_sourceTable));
    }

    /// <summary>
    /// A standalone reload replication has no watermark and no change feed — it re-reads its source
    /// every pass, divided into the segments configured on its reader. Those are iterated within one
    /// Primary pass, so the mapping still has exactly one run per pass rather than one per segment.
    /// </summary>
    [Fact]
    public async Task StandaloneReloadReplication_IteratesEverySegmentConfiguredOnItsReader()
    {
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One'), (5, 'Five'), (9, 'Nine'), (40, 'Forty');");

        SetUpReloadReplication(SegmentSerializer.SerializeMany(
            [new RangeSegment("Id", "1", "6"), new RangeSegment("Id", "6", "11")]));

        var runId = _workQueueStore.Enqueue("reload-only", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("reload-only", degreeOfParallelism: 1, CancellationToken.None);

        var run = _taskRunStore.GetRun(runId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        // Both segments contributed to the one run's totals; Id 40 is outside both and never read.
        Assert.Equal(3, run.RowsRead);

        var rows = await GetRowsAsync(_reloadTargetTable);
        Assert.Equal(["One", "Five", "Nine"], rows.OrderBy(r => r.Key).Select(r => r.Value));
    }

    /// <summary>An Auto segment in a standalone reload's configured list is resolved against the
    /// source's live value range at run time, not at config-save time — the range moves as the table
    /// does, so pinning it when the config was written would go stale immediately.</summary>
    [Fact]
    public async Task StandaloneReloadReplication_ExpandsAnAutoSegmentAgainstTheLiveSource()
    {
        await ExecuteAsync(_adminConnection, $"""
            INSERT INTO dbo.[{_sourceTable}] (Id, Name)
            SELECT n, CONCAT('Row', n) FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8)) v(n);
            """);

        SetUpReloadReplication(SegmentSerializer.SerializeMany([new AutoSegment("Id", 3)]));

        var runId = _workQueueStore.Enqueue("reload-only", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("reload-only", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        // Every row lands exactly once — the buckets have to tile the range and cover MAX for this
        // count to come out right.
        Assert.Equal(8, (await GetRowsAsync(_reloadTargetTable)).Count);
    }

    private void SetUpReloadReplication(string segmentsJson)
    {
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "reload-only",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig
                {
                    Kind = MsSqlDriverKinds.BatchReload,
                    Options = { [SegmentSerializer.SegmentsOptionKey] = segmentsJson },
                },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.MergeReconcile },
            },
        }, Author);

        _configRepository.SaveTableMapping("reload-only", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableRef { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = _reloadTargetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, Author);
    }
}
