using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Scripting;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using DbDataSync.TaskRunner;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// The one true end-to-end test of the v1 vertical slice: real git-backed config (Phase 1), real
/// SQLite state (Phase 2), the real MSSQL driver (Phase 3), and RunExecutor (Phase 4) all wired
/// together and pointed at a real SQL Server, exactly as a hand invocation of the compiled
/// DbDataSync.TaskRunner executable would be. Needs the same Docker SQL Server container as
/// DbDataSync.Drivers.MsSql.Tests — see that project's MsSqlTestDatabase for the connection string
/// convention (duplicated here rather than shared: this is only the second consumer of that fixture
/// shape, and it's ~30 lines — see architecture/implementation/done/phase-003-mssql-driver.md's notes on
/// when to extract a shared test-support project instead).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunExecutorIntegrationTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly string _databaseName = $"DbDataSyncTaskRunnerTest_{Guid.NewGuid():N}";
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-taskrunner-e2e-").FullName;
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _reloadTargetTable = $"ReloadTgt_{Guid.NewGuid():N}";

    private readonly MsSqlDriver _driver = new();

    private ConfigRepository _configRepository = null!;
    private RunExecutor _executor = null!;
    private WorkQueueStore _workQueueStore = null!;
    private TaskRunStore _taskRunStore = null!;
    private ChangeWatermarkStore _watermarkStore = null!;
    private LogWriter _logWriter = null!;
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
        _logWriter = new LogWriter(stateDatabase);
        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore,
            new LocalRunnerState(_taskRunStore, _workQueueStore, new RunLockStore(stateDatabase),
                _watermarkStore, new VerificationResultStore(stateDatabase), _logWriter),
            // The real thing, not a fake: what this fixture wants to be able to assert is that a
            // provisioning report becomes a committed change to the mapping on disk, which is
            // LocalRunnerConfig's whole job. In a deployment the runner reaches it over loopback.
            new LocalRunnerConfig(_configRepository, Author),
            Scripting.ForTests(_configRepository, _repoRoot),
            Path.Combine(_repoRoot, "state.db"));

        await SetUpConfigAsync();
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

    /// <summary>
    /// Saves a mapping with its phase-90 column cache populated from the real catalog first — which is
    /// what makes it a mapping phase 91's readers and writers will run.
    /// <para>
    /// A mapping created through the app gets this for free: the editor sends the columns it already
    /// fetched, and Refresh metadata re-reads them. These fixtures build a
    /// <see cref="TableMappingConfig"/> in C# and hand it straight to <see cref="ConfigRepository"/>,
    /// below the API layer where that capture lives, so without this they save a mapping with an empty
    /// cache and every consumer named in phase 91 throws <c>MetadataNotCachedException</c> on the first
    /// pass. Introspection goes through <see cref="IDriver.ListColumnsAsync"/> — the same public seam
    /// the API's own capture reads — rather than a hand-written column list, so the cache says what the
    /// server actually says.
    /// </para>
    /// </summary>
    private async Task SaveMappingAsync(string replicationName, TableMappingConfig mapping)
    {
        mapping.SourceColumns = await CachedColumnsAsync(mapping.Sources[0].Schema, mapping.Sources[0].Table);
        mapping.TargetColumns = await CachedColumnsAsync(mapping.Targets[0].Schema, mapping.Targets[0].Table);
        _configRepository.SaveTableMapping(replicationName, mapping, Author);
    }

    /// <summary>
    /// One table's shape as the cache stores it. A table that does not exist is left uncaptured rather
    /// than being an error here — a real mapping pointed at a missing table has nothing to capture
    /// either, and <see cref="AFailedPass_DoesNotAcknowledgeAndLeavesTheHistoryAlone"/> aims a writer
    /// at one on purpose. Its pass still fails, which is all that test asks of it; only the message
    /// changes, from the target table not existing to its metadata never having been cached.
    /// </summary>
    private async Task<List<CachedColumn>> CachedColumnsAsync(string schema, string table)
    {
        IReadOnlyList<ColumnMetadata> columns;
        try
        {
            columns = await _driver.ListColumnsAsync(
                _adminConnection, _databaseName, schema, table, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return [.. columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))];
    }

    private async Task SetUpConfigAsync(int frequencySeconds = 1, int idleTimeoutSeconds = 2, bool traceTiming = false)
    {
        ConnectionInput MakeConnectionInput(string name) => new()
        {
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
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

        await SaveMappingAsync("e2e-sync", new TableMappingConfig
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
        });
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
        await _executor.ExecuteWorkerAsync("e2e-sync", WorkerLanes.Uniform(1), CancellationToken.None);
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
        await SetUpConfigAsync(frequencySeconds: 2, idleTimeoutSeconds: 6);

        var first = _workQueueStore.Enqueue("e2e-sync", RunKind.Primary, "main");
        var worker = _executor.ExecuteWorkerAsync("e2e-sync", WorkerLanes.Uniform(1), CancellationToken.None);

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
    /// All four timestamps, through the real worker rather than through direct store calls: the enqueue
    /// writes EnqueuedAtUtc, TryClaimNext writes ClaimedAtUtc, BeginRun writes StartedAtUtc and
    /// CompleteRun writes EndedAtUtc. Each on the path a worker actually takes, which is what a
    /// unit test calling the stores in order cannot show.
    /// <para>
    /// The assertion that matters is the ordering — enqueued, claimed, started, ended — because it is
    /// the property both derived figures depend on and the one a wrong write site would break. The
    /// magnitudes are not asserted: this drains the queue immediately, so the real gaps here are
    /// microseconds, and a threshold on them would be a clock-resolution flake rather than a fact about
    /// the code. The deliberate-gap cases are asserted in TaskRunStoreTests.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWorkerRunningARun_RecordsEnqueueClaimStartAndEnd_InThatOrder()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var run = await EnqueueAndDrainAsync();

        Assert.NotNull(run.EnqueuedAtUtc);
        Assert.NotNull(run.ClaimedAtUtc);
        Assert.NotNull(run.StartedAtUtc);
        Assert.NotNull(run.EndedAtUtc);
        Assert.True(run.ClaimedAtUtc >= run.EnqueuedAtUtc, "a run cannot be claimed before it was queued");
        Assert.True(run.StartedAtUtc >= run.ClaimedAtUtc, "a run cannot start before it was claimed");
        Assert.True(run.EndedAtUtc >= run.StartedAtUtc, "a run cannot end before it started");
    }

    #region Phase 74 — two mappings, one source table

    /// <summary>
    /// Adds a second mapping over the same source table, with its own target and its own reader, and
    /// returns its name. The shape phase 74 exists for: nothing in the config model stops it, and
    /// under the old <c>(TaskName, SourceTable)</c> key the two shared one stored position.
    /// </summary>
    private async Task<string> AddSecondMappingOnTheSameTableAsync(string name, ReaderConfig readerOverride)
    {
        var targetTable = $"Tgt_{Guid.NewGuid():N}";
        await ExecuteAsync(_adminConnection,
            $"CREATE TABLE dbo.[{targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

        await SaveMappingAsync("e2e-sync", new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
            ReaderOverride = readerOverride,
        });
        return name;
    }

    /// <summary>Drains one pass per named mapping through the real worker.</summary>
    private async Task DrainMappingsAsync(params string[] mappingNames)
    {
        var ids = mappingNames.Select(name => _workQueueStore.Enqueue("e2e-sync", RunKind.Primary, name)).ToList();
        await _executor.ExecuteWorkerAsync("e2e-sync", WorkerLanes.Uniform(1), CancellationToken.None);
        foreach (var id in ids)
        {
            // A failed pass writes no watermark, so without this a broken run reads as "the two
            // mappings kept separate positions" — null is separate from anything.
            var run = _taskRunStore.GetRun(id)!;
            Assert.True(run.Status == RunStatus.Succeeded, $"{run.MappingName}: {run.Status} — {run.ErrorSummary}");
        }
    }

    /// <summary>
    /// The bug that motivated phase 74, through the real worker: two mappings on one physical source
    /// table, reading it two incompatible ways.
    /// <para>
    /// Change Tracking's position is a database-wide version number; the generic Watermark reader's is
    /// whatever is in its watermark column. Sharing one row, whichever pass ran last overwrote the
    /// other with a value it could not interpret — the version reader resuming from a row id, or the
    /// other way round. The Ids here are deliberately far from any plausible change-tracking version,
    /// so the two positions cannot pass this test by coinciding.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoMappingsOnOneSourceTable_WithDifferentReaders_KeepTheirOwnPositions()
    {
        var watermarkMapping = await AddSecondMappingOnTheSameTableAsync(
            "by-id",
            new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = { ["watermarkColumn"] = "Id" } });

        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (500, 'Alice'), (501, 'Bob');");

        await DrainMappingsAsync("main", watermarkMapping);

        var changeTracking = WatermarkFor("e2e-sync", "main", _sourceTable);
        var byId = WatermarkFor("e2e-sync", watermarkMapping, _sourceTable);

        // The generic reader records the highest value it saw in its own column; Change Tracking
        // records the database's version, which is a small counter and not 501.
        Assert.Equal("501", byId);
        Assert.NotNull(changeTracking);
        Assert.NotEqual(byId, changeTracking);

        // And they stay apart across a second pass, which is where a shared row did its damage: the
        // first mapping's next read would have started from the other's number.
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (502, 'Carol');");
        await DrainMappingsAsync("main", watermarkMapping);

        Assert.Equal("502", WatermarkFor("e2e-sync", watermarkMapping, _sourceTable));
        Assert.NotEqual("502", WatermarkFor("e2e-sync", "main", _sourceTable));
    }

    /// <summary>
    /// The quieter half of the same bug, and the worse one: the same reader kind on the same table,
    /// differing only in a per-mapping option. Nothing fails and nothing is unparseable — the two
    /// simply overwrite each other with positions measured along different columns, so each mapping
    /// resumes from a place in the table it never reached.
    /// </summary>
    [Fact]
    public async Task TwoMappingsOnOneSourceTable_WithDifferentWatermarkColumns_KeepTheirOwnPositions()
    {
        // A second candidate column, an order of magnitude away from Id so a position measured along
        // one cannot be mistaken for a position along the other.
        await ExecuteAsync(_adminConnection, $"ALTER TABLE dbo.[{_sourceTable}] ADD Seq INT NULL;");

        var byId = await AddSecondMappingOnTheSameTableAsync(
            "by-id",
            new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = { ["watermarkColumn"] = "Id" } });
        var bySeq = await AddSecondMappingOnTheSameTableAsync(
            "by-seq",
            new ReaderConfig { Kind = GenericDriverKinds.Watermark, Options = { ["watermarkColumn"] = "Seq" } });

        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name, Seq) VALUES (500, 'Alice', 9000), (501, 'Bob', 9001);");

        await DrainMappingsAsync(byId, bySeq);

        Assert.Equal("501", WatermarkFor("e2e-sync", byId, _sourceTable));
        Assert.Equal("9001", WatermarkFor("e2e-sync", bySeq, _sourceTable));
    }

    #endregion

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

    private string WatermarkFor(string table) => WatermarkFor("e2e-sync", "main", table)!;

    private string? WatermarkFor(string taskName, string mappingName, string table) =>
        _watermarkStore.GetWatermark(taskName, mappingName, WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = table },
            MsSqlDialect.Instance));

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
        await _executor.ExecuteWorkerAsync("e2e-sync", WorkerLanes.Uniform(1), CancellationToken.None);

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

        await SetUpReloadReplicationAsync([new RangeSegment("Id", "1", "6"), new RangeSegment("Id", "6", "11")]);

        var runId = _workQueueStore.Enqueue("reload-only", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("reload-only", WorkerLanes.Uniform(1), CancellationToken.None);

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

        await SetUpReloadReplicationAsync(([new AutoSegment("Id", 3)]));

        var runId = _workQueueStore.Enqueue("reload-only", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("reload-only", WorkerLanes.Uniform(1), CancellationToken.None);

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
        await SetUpConfigAsync();
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One');");

        var run = await EnqueueAndDrainAsync();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Null(run.Timing);
    }

    [Fact]
    public async Task WithTheTraceOption_ARunRecordsEveryStage()
    {
        await SetUpConfigAsync(traceTiming: true);
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
        await SetUpConfigAsync(traceTiming: true);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'One');");
        await EnqueueAndDrainAsync();

        // Second pass: nothing changed, so the reader produces no rows — but it still ran, and a run
        // reporting nothing at all would hide a source that is slow to answer "no changes".
        var run = await EnqueueAndDrainAsync();

        Assert.NotNull(run.Timing);
        Assert.NotNull(run.Timing!.ReaderLifetimeMs);
        Assert.Null(run.Timing.ReaderTimeToFirstRowMs);
    }

    private async Task SetUpReloadReplicationAsync(IReadOnlyList<BatchReloadSegment> segments)
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

        await SaveMappingAsync("reload-only", new TableMappingConfig
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
        });
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

        await SaveMappingAsync(replicationName, new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        });
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
        await _executor.ExecuteWorkerAsync("trg-sync", WorkerLanes.Uniform(1), CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(first)!.Status);

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        Assert.True(await ShadowRowCountAsync() > 0);

        var second = _workQueueStore.Enqueue("trg-sync", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("trg-sync", WorkerLanes.Uniform(1), CancellationToken.None);

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
        await _executor.ExecuteWorkerAsync("trg-fail", WorkerLanes.Uniform(1), CancellationToken.None);

        var run = _taskRunStore.GetRun(runId)!;
        Assert.Equal(RunStatus.Failed, run.Status);

        // Nor in the run's own history: the pass had computed a position before the write failed, and
        // recording it here would claim an advance that never became durable (phase 71).
        Assert.Null(run.PreviousWatermark);
        Assert.Null(run.NewWatermark);

        // Not pruned, and no watermark stored — the two go together, and that pairing is the invariant.
        Assert.True(await ShadowRowCountAsync() > 0);
        Assert.Null(WatermarkFor("trg-fail", "main", _sourceTable));
    }

    #endregion

    #region Auto-provisioning reports what it provisioned (phase 94)

    /// <summary>
    /// A replication that provisions its own target, with a mapping saved the way a real one would be:
    /// through the same capture every other fixture here uses, which finds nothing to capture for a
    /// table that does not exist yet.
    /// </summary>
    private async Task SetUpProvisioningAsync(
        string replicationName, string targetTable, bool create = false, bool alter = false)
    {
        _configRepository.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = replicationName,
            Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2,
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
            Provisioning = new ProvisioningConfig
            {
                CreateTargetTableIfMissing = create,
                AlterTargetTableColumnsIfMissingOrChanged = alter,
            },
        }, Author);

        await SaveMappingAsync(replicationName, new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        });
    }

    /// <summary>
    /// **The gap phase 94 closes, end to end.** A mapping whose target does not exist has nothing to
    /// capture when it is saved, so its phase-90 cache is empty; phase 91's writers refuse to run on an
    /// empty cache. Provisioning creating the table and saying nothing about it left that mapping
    /// failing its own first pass — correct table, failed run — until an operator pressed Refresh
    /// metadata once. It succeeds here on the first pass, with nothing pressed.
    /// </summary>
    [Fact]
    public async Task AMappingThatProvisionsItsOwnTarget_SucceedsOnItsFirstPass()
    {
        var target = $"ProvTgt_{Guid.NewGuid():N}";
        await SetUpProvisioningAsync("prov-create", target, create: true);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        // The premise: nothing was cached at save time, because there was no table to read.
        Assert.Empty(_configRepository.LoadTableMapping("prov-create", "main").TargetColumns);

        var runId = _workQueueStore.Enqueue("prov-create", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-create", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await GetRowsAsync(target));
    }

    /// <summary>
    /// The durable half. The pass above would also pass on an in-memory fix alone — it holds the
    /// mapping it provisioned for. This one re-reads the mapping from disk, which is what the *next*
    /// pass and every other process does, and is the only thing that proves the report reached the
    /// owner and was committed rather than living in one run's memory.
    /// </summary>
    [Fact]
    public async Task TheProvisionedShape_IsOnDiskForTheNextPassToRead()
    {
        var target = $"ProvTgt_{Guid.NewGuid():N}";
        await SetUpProvisioningAsync("prov-create", target, create: true);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        _workQueueStore.Enqueue("prov-create", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-create", WorkerLanes.Uniform(1), CancellationToken.None);

        var reloaded = _configRepository.LoadTableMapping("prov-create", "main");
        Assert.Equal(["Id", "Name"], reloaded.TargetColumns.Select(c => c.Name));
        Assert.NotNull(reloaded.ColumnsCapturedUtc);

        // The catalog's own answer, not the plan's request — which is why the cache can be trusted by
        // the consumers that build DDL from these type strings.
        Assert.Equal(
            await CachedColumnsAsync("dbo", target),
            reloaded.TargetColumns,
            (a, b) => a.Name == b.Name && a.SameShapeAs(b));

        // And a second pass runs from it, reading a freshly-loaded mapping like any other process.
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (2, 'Bob');");
        var second = _workQueueStore.Enqueue("prov-create", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-create", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(second)!.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await GetRowsAsync(target));
    }

    /// <summary>
    /// The alter path reports its result on the same terms the create path does. Both produce a target
    /// whose shape only they know, and a cache left describing the table as it was before the ALTER is
    /// a cache missing the column the pass just added.
    /// </summary>
    [Fact]
    public async Task AnAlteredTarget_ReportsTheShapeItEndedUpWith()
    {
        var target = $"AlterTgt_{Guid.NewGuid():N}";
        await ExecuteAsync(_adminConnection,
            $"CREATE TABLE dbo.[{target}] (Id INT NOT NULL PRIMARY KEY);");
        await SetUpProvisioningAsync("prov-alter", target, alter: true);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        // The premise here is the opposite of the create case: the table existed, so its shape *was*
        // captured — accurately, and without the mapped column the pass is about to add.
        Assert.Equal(["Id"], _configRepository.LoadTableMapping("prov-alter", "main").TargetColumns.Select(c => c.Name));

        var runId = _workQueueStore.Enqueue("prov-alter", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-alter", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await GetRowsAsync(target));
        Assert.Equal(
            ["Id", "Name"],
            _configRepository.LoadTableMapping("prov-alter", "main").TargetColumns.Select(c => c.Name));
    }

    #endregion

    #region An empty cache recovers even with provisioning off (phase 97)

    /// <summary>The one line the recovery inspection writes, which is how these two tests tell "it
    /// looked" from "it did not".</summary>
    private const string InspectedToRecover = "inspecting the target to recover it";

    private bool LoggedInspection(Guid runId) =>
        _logWriter.GetLogs(runId).Any(e => e.Message.Contains(InspectedToRecover, StringComparison.Ordinal));

    /// <summary>
    /// **The gap phase 97 closes.** Phase 94's recovery — a target already in shape whose shape has
    /// never been cached — sat below the <c>mayCreate</c>/<c>mayAlter</c> early return, so it could
    /// never fire for a mapping with both settings off. Which is every mapping provisioned by hand,
    /// since that is *why* it was provisioned by hand: the operator applied the DDL themselves and left
    /// automation off, and the run then refused to look at a table it could see was correct.
    /// <para>
    /// The fixture is that sequence exactly: save the mapping while the target is missing (nothing to
    /// capture, so an empty cache), then create the target by hand in the shape the mapping wants. No
    /// Refresh is pressed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AMappingWithProvisioningOff_RecoversAnEmptyCacheFromATargetAlreadyInShape()
    {
        var target = $"HandTgt_{Guid.NewGuid():N}";
        await SetUpProvisioningAsync("prov-off", target);

        Assert.Empty(_configRepository.LoadTableMapping("prov-off", "main").TargetColumns);

        // Provisioned by hand, after the mapping was described — the Apply button's outcome, or an
        // operator's own script.
        await ExecuteAsync(_adminConnection,
            $"CREATE TABLE dbo.[{target}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var runId = _workQueueStore.Enqueue("prov-off", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-off", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice" }, await GetRowsAsync(target));
        Assert.True(LoggedInspection(runId));

        // On disk, so the next pass never has to look again — and the catalog's answer, not the plan's.
        var reloaded = _configRepository.LoadTableMapping("prov-off", "main");
        Assert.Equal(["Id", "Name"], reloaded.TargetColumns.Select(c => c.Name));
        Assert.NotNull(reloaded.ColumnsCapturedUtc);
        Assert.Equal(
            await CachedColumnsAsync("dbo", target),
            reloaded.TargetColumns,
            (a, b) => a.Name == b.Name && a.SameShapeAs(b));
    }

    /// <summary>
    /// The other half of that bargain: recovery is for the pass that needs it and costs nothing after.
    /// The cache is tested *before* a plan is asked for, so a mapping with provisioning off and a
    /// populated cache leaves <c>EnsureTargetTableProvisionedAsync</c> at the same early return it
    /// always did — no plan, no catalog read, and nothing rewritten.
    /// <para>
    /// The stamp is the sharp end. It moves whenever a capture is recorded, so asserting it did not
    /// move is asserting no second picture was taken of a table nobody had a reason to look at.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AMappingWithProvisioningOffAndAFullCache_InspectsNothingOnALaterPass()
    {
        var target = $"HandTgt_{Guid.NewGuid():N}";
        await ExecuteAsync(_adminConnection,
            $"CREATE TABLE dbo.[{target}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

        // Saved with the target already there, so the capture at save time filled the cache — the
        // steady state every mapping reaches once, however it got there.
        await SetUpProvisioningAsync("prov-off", target);
        var captured = _configRepository.LoadTableMapping("prov-off", "main").ColumnsCapturedUtc;
        Assert.Equal(["Id", "Name"], _configRepository.LoadTableMapping("prov-off", "main").TargetColumns.Select(c => c.Name));

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");
        var runId = _workQueueStore.Enqueue("prov-off", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-off", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, _taskRunStore.GetRun(runId)!.Status);
        Assert.False(LoggedInspection(runId));
        Assert.Equal(captured, _configRepository.LoadTableMapping("prov-off", "main").ColumnsCapturedUtc);
    }

    /// <summary>
    /// The line the hoist must not cross. A mapping with both settings off and a target that does not
    /// exist reaches a <c>Missing</c> create plan it is not allowed to run — and must run none of it.
    /// This is the assertion behind the claim that phase 97 leaves "provisioning off" meaning exactly
    /// what it meant: the pass fails on the missing table, and the table is still missing after.
    /// </summary>
    [Fact]
    public async Task AMappingWithProvisioningOff_NeverCreatesTheTargetItInspectedFor()
    {
        var target = $"NeverTgt_{Guid.NewGuid():N}";
        await SetUpProvisioningAsync("prov-off", target);
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice');");

        var runId = _workQueueStore.Enqueue("prov-off", RunKind.Primary, "main");
        await _executor.ExecuteWorkerAsync("prov-off", WorkerLanes.Uniform(1), CancellationToken.None);

        Assert.Equal(RunStatus.Failed, _taskRunStore.GetRun(runId)!.Status);
        Assert.Empty(await CachedColumnsAsync("dbo", target));
        Assert.Empty(_configRepository.LoadTableMapping("prov-off", "main").TargetColumns);
    }

    #endregion
}
