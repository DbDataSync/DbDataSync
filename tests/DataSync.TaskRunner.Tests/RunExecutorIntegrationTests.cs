using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Scripting;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.Drivers.Generic;
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
            _configRepository, driverRegistry, secretStore,
            new LocalRunnerState(_taskRunStore, _workQueueStore, new RunLockStore(stateDatabase),
                _watermarkStore, new VerificationResultStore(stateDatabase), new LogWriter(stateDatabase)),
            Scripting.ForTests(_configRepository, _repoRoot),
            Path.Combine(_repoRoot, "state.db"));

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

    private void SetUpConfig(int frequencySeconds = 1, int idleTimeoutSeconds = 2, bool traceTiming = false)
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
            Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous,
                // A continuous worker now stays resident until it has gone a whole idle timeout
                // without a pass reading anything. These tests drain a queue and want the worker to
                // leave promptly, so they say so — the production default is 60 seconds.
                FrequencySeconds = frequencySeconds,
                IdleTimeoutSeconds = idleTimeoutSeconds,
            },
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
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = _targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
            TraceTiming = traceTiming,
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

    /// <summary>
    /// One worker, two passes, real rows moving between them — the thing the old exit rule made
    /// impossible.
    /// <para>
    /// It exited as soon as the queue was empty, which is true for a fraction of a second after every
    /// pass, so a live load produced spawn-run-exit-respawn several times a minute. Here the second
    /// pass is enqueued *while the worker is waiting out its interval*, and it lands inside the same
    /// worker lifetime: one `ExecuteWorkerAsync` call, two succeeded runs, and the rows to prove the
    /// second one did real work.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AResidentWorker_RunsSuccessivePassesWithoutBeingRespawned()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        // Long enough that the worker is still waiting when the second pass arrives, short enough that
        // the test ends in seconds.
        SetUpConfig(frequencySeconds: 2, idleTimeoutSeconds: 6);

        var first = _workQueueStore.Enqueue("e2e-sync", RunKind.Primary, "main");
        var worker = _executor.ExecuteWorkerAsync("e2e-sync", degreeOfParallelism: 1, CancellationToken.None);

        // Wait for the first pass to land, then produce more work for a worker that is already idle.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && _taskRunStore.GetRun(first)?.Status != RunStatus.Succeeded)
            await Task.Delay(100);
        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(first)!.Status);

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        var second = _workQueueStore.Enqueue("e2e-sync", RunKind.Primary, "main");

        // The same call is still running — nothing respawned a worker, because nothing had to.
        Assert.False(worker.IsCompleted, "the worker left between passes.");
        await worker;

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(second)!.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await GetTargetRowsAsync());
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

    /// <summary>
    /// Phase 71's history, end to end. ChangeWatermarks says only where a mapping is *now*; these two
    /// columns on each run are the only record of how it got there, and each row has to be readable on
    /// its own — hence recording where the pass started as well as where it ended.
    /// </summary>
    [Fact]
    public async Task EachSuccessfulPass_RecordsTheWatermarkItMovedFromAndTo()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var firstRun = await EnqueueAndDrainAsync();

        // A first pass has nowhere to have come from, which is a different answer from "it did not
        // move" — so previous is null and new is not.
        Assert.Null(firstRun.PreviousWatermark);
        Assert.NotNull(firstRun.NewWatermark);

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        var secondRun = await EnqueueAndDrainAsync();

        // The chain: the second pass starts exactly where the first one left off, and ends at what
        // ChangeWatermarks now holds. That equality is what makes the history trustworthy rather than
        // a parallel number maintained beside the real one.
        Assert.Equal(firstRun.NewWatermark, secondRun.PreviousWatermark);
        Assert.Equal(WatermarkFor(_sourceTable), secondRun.NewWatermark);
    }

    /// <summary>
    /// Phase 72's timestamps, through the real worker rather than through a direct BeginRun call: the
    /// enqueue writes StartedAtUtc, the worker claiming the item writes ClaimedAtUtc, and the two are
    /// what duration and queue wait are each measured from.
    /// <para>
    /// The assertion that matters is the ordering — enqueued, then claimed, then ended — because it is
    /// the property both derived figures depend on and the one a wrong write site would break. The
    /// magnitudes are not asserted: this drains the queue immediately, so the real wait here is
    /// microseconds, and a threshold on it would be a clock-resolution flake rather than a fact about
    /// the code. The deliberate-gap case is asserted in TaskRunStoreTests.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWorkerClaimingARun_RecordsTheClaimBetweenTheEnqueueAndTheEnd()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var run = await EnqueueAndDrainAsync();

        Assert.NotNull(run.ClaimedAtUtc);
        Assert.NotNull(run.EndedAtUtc);
        Assert.True(run.ClaimedAtUtc >= run.StartedAtUtc, "a run cannot be claimed before it was queued");
        Assert.True(run.EndedAtUtc >= run.ClaimedAtUtc, "a run cannot end before it was claimed");
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
    /// The invariant the whole run model rests on: a watermark records "everything up to here is at
    /// the target", so it may only advance once the target write has committed. A pass that read the
    /// changes and then failed to write them must leave the watermark where it was — otherwise those
    /// rows are skipped by every pass afterwards and the two sides silently diverge, with nothing
    /// left to notice it by.
    /// </summary>
    [Fact]
    public async Task AFailedWrite_LeavesTheWatermarkWhereItWas_SoTheChangesAreNotSkipped()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");
        Assert.Equal(RunStatus.Succeeded, (await EnqueueAndDrainAsync()).Status);
        var watermarkBefore = WatermarkFor(_sourceTable);

        // Changes the pass will read, and a target it cannot write them to. The read succeeds, so the
        // watermark this pass *would* record is a real, later one.
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        await ExecuteAsync(_adminConnection, $"DROP TABLE dbo.[{_targetTable}];");

        var failed = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Equal(watermarkBefore, WatermarkFor(_sourceTable));

        // And the proof that it matters: with the target back, the next pass still sees Bob.
        await ExecuteAsync(_adminConnection,
            $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

        Assert.Equal(RunStatus.Succeeded, (await EnqueueAndDrainAsync()).Status);

        // Bob, and only Bob: Alice went with the dropped table, and the watermark correctly says she
        // was already delivered, so an incremental pass has no reason to send her again. Bob is the
        // change that the failed pass read — and that a wrongly-advanced watermark would have lost.
        Assert.Equal(new Dictionary<int, string> { [2] = "Bob" }, await GetTargetRowsAsync());
    }

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

        SetUpReloadReplication([new RangeSegment("Id", "1", "6"), new RangeSegment("Id", "6", "11")]);

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

        SetUpReloadReplication(([new AutoSegment("Id", 3)]));

        var runId = _workQueueStore.Enqueue("reload-only", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("reload-only", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        // Every row lands exactly once — the buckets have to tile the range and cover MAX for this
        // count to come out right.
        Assert.Equal(8, (await GetRowsAsync(_reloadTargetTable)).Count);
    }

    /// <summary>
    /// Phase 59's opt-in trace: off by default, so an unopted-in mapping's run carries no timing at
    /// all rather than a row of zeros.
    /// </summary>
    [Fact]
    public async Task WithoutTheTraceOption_ARunRecordsNoTiming()
    {
        SetUpConfig();
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One');");

        var run = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Null(run.Timing);
    }

    [Fact]
    public async Task WithTheTraceOption_ARunRecordsEveryStage()
    {
        SetUpConfig(traceTiming: true);
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One'), (2, 'Two'), (3, 'Three');");

        var run = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var timing = run.Timing;
        Assert.NotNull(timing);

        // The Kinds this pass actually used, which for a Primary pass are the replication's own.
        Assert.Equal(MsSqlDriverKinds.ChangeTracking, timing!.ReaderKind);
        Assert.Equal(MsSqlDriverKinds.StagingTable, timing.StagingKind);
        Assert.Equal(MsSqlDriverKinds.Merge, timing.WriterKind);

        Assert.NotNull(timing.ReaderTimeToFirstRowMs);
        Assert.NotNull(timing.ReaderLifetimeMs);
        Assert.NotNull(timing.StagingDurationMs);
        Assert.NotNull(timing.WriterDurationMs);

        // The invariant worth asserting directly: time-to-first-row is a prefix of the lifetime.
        Assert.True(timing.ReaderTimeToFirstRowMs <= timing.ReaderLifetimeMs,
            $"first row at {timing.ReaderTimeToFirstRowMs}ms, lifetime {timing.ReaderLifetimeMs}ms");

        // Nothing is asserted between the staging clock and the reader's: they start at different
        // reference points (the reader's before ReadChangesAsync is called, staging's only after it has
        // returned), so neither span nests inside the other. See phase 66.
    }

    [Fact]
    public async Task WithTheTraceOption_AReadThatFoundNothing_StillRecordsALifetime()
    {
        SetUpConfig(traceTiming: true);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One');");
        await EnqueueAndDrainAsync();

        // Second pass: nothing changed, so the reader produces no rows — but it still ran, and a run
        // reporting nothing at all would hide a source that is slow to answer "no changes".
        var run = await EnqueueAndDrainAsync();

        Assert.NotNull(run.Timing);
        Assert.NotNull(run.Timing!.ReaderLifetimeMs);
        Assert.Null(run.Timing.ReaderTimeToFirstRowMs);
    }

    private void SetUpReloadReplication(IReadOnlyList<BatchReloadSegment> segments)
    {
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "reload-only",
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
                Reader = new ReaderConfig
                {
                    Kind = MsSqlDriverKinds.BatchReload,
                },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.MergeReconcile },
            },
        }, Author);

        _configRepository.SaveTableMapping("reload-only", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = _reloadTargetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
            // On the mapping, not on the reader's options bag — phase 58 moved segmenting to where it
            // belongs and stopped reading the old `segments` reader option entirely.
            DefaultSegmenting = [.. segments],
        }, Author);
    }

    #region Position acknowledgement

    /// <summary>
    /// Sets up a second replication reading the source through the generic trigger-audit reader, with
    /// pruning on. <paramref name="targetTable"/> is what the writer aims at — pointed at a table that
    /// does not exist to make the pass fail after the read.
    /// </summary>
    private async Task SetUpTriggerAuditAsync(string replicationName, string targetTable)
    {
        await ExecuteAsync(_adminConnection, MsSqlTriggerAudit.CreateShadowTable(
            "dbo", _sourceTable, ["[Id] INT NOT NULL"]));
        await ExecuteAsync(_adminConnection, MsSqlTriggerAudit.CreateTrigger("dbo", _sourceTable, ["Id"]));

        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = replicationName,
            Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2,
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig
                {
                    Kind = GenericDriverKinds.TriggerAudit,
                    Options = new Dictionary<string, string> { [TriggerAuditReader.PruneOption] = "true" },
                },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, Author);

        _configRepository.SaveTableMapping(replicationName, new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, Author);
    }

    private async Task<long> ShadowRowCountAsync()
    {
        await using var cmd = _adminConnection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{TriggerAuditStatement.ShadowTableName(_sourceTable)}];";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// A successful pass tells the source its position is durable, and the shadow table is pruned —
    /// which is the only thing stopping it growing for as long as the replication runs.
    /// </summary>
    [Fact]
    public async Task ASuccessfulPass_AcknowledgesItsPositionAndPrunes()
    {
        await SetUpTriggerAuditAsync("trg-sync", _targetTable);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        // The first pass is a full load, which stores a position without reading the shadow table.
        // The second is the incremental one whose position is worth acknowledging.
        var first = _workQueueStore.Enqueue("trg-sync", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("trg-sync", degreeOfParallelism: 1, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(first)!.Status);

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        Assert.True(await ShadowRowCountAsync() > 0);

        var second = _workQueueStore.Enqueue("trg-sync", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("trg-sync", degreeOfParallelism: 1, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(second)!.Status);
        Assert.Equal(0, await ShadowRowCountAsync());
    }

    /// <summary>
    /// **The assertion that protects the retry.** A pass that fails must not tell the source to
    /// discard the changes it just read — do that and the failure stops being retryable, and a bad
    /// pass becomes permanent data loss. Here the write fails because the target does not exist, and
    /// the shadow rows are still there afterwards.
    /// </summary>
    [Fact]
    public async Task AFailedPass_DoesNotAcknowledgeAndLeavesTheHistoryAlone()
    {
        await SetUpTriggerAuditAsync("trg-fail", $"NotATable_{Guid.NewGuid():N}");
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var runId = _workQueueStore.Enqueue("trg-fail", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("trg-fail", degreeOfParallelism: 1, CancellationToken.None);

        var run = _taskRunStore.GetRun(runId)!;
        Assert.Equal(RunStatus.Failed, run.Status);

        // Nor in the run's own history: the pass had computed a position before the write failed, and
        // recording it here would claim an advance that never became durable (phase 71).
        Assert.Null(run.PreviousWatermark);
        Assert.Null(run.NewWatermark);

        // Not pruned, and no watermark stored — the two go together, and that pairing is the invariant.
        Assert.True(await ShadowRowCountAsync() > 0);
        Assert.Null(_watermarkStore.GetWatermark(
            "trg-fail", $"src-conn/{_databaseName}/dbo.{_sourceTable}"));
    }

    #endregion
}
