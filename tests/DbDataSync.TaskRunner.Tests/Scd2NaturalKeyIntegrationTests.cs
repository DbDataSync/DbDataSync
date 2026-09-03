using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Scripting;
using DbDataSync.State;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// Phase 68 end to end: one replication configured for the SCD Type 2 writer with no natural key of
/// its own, two table mappings with different primary keys, each deriving and versioning by its own.
/// <para>
/// The same container and fixture shape as <see cref="RunExecutorIntegrationTests"/>. Its own class
/// rather than more methods on that one because these need two source tables with different keys and
/// a replication whose writer is not <c>MsSqlMerge</c>, which is most of that fixture's setup.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Scd2NaturalKeyIntegrationTests : IAsyncLifetime
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private const string TaskName = "scd2-sync";

    private readonly string _databaseName = $"DbDataSyncScd2Test_{Guid.NewGuid():N}";
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-scd2-e2e-").FullName;

    // Single-column key.
    private readonly string _orders = $"Orders_{Guid.NewGuid():N}";
    private readonly string _ordersTarget = $"OrdersHist_{Guid.NewGuid():N}";

    // Composite key — the case a replication-wide natural key could never have expressed alongside
    // the one above.
    private readonly string _lines = $"Lines_{Guid.NewGuid():N}";
    private readonly string _linesTarget = $"LinesHist_{Guid.NewGuid():N}";

    private ConfigRepository _configRepository = null!;
    private RunExecutor _executor = null!;
    private WorkQueueStore _workQueueStore = null!;
    private TaskRunStore _taskRunStore = null!;
    private SqlConnection _adminConnection = null!;
    private LogWriter _logWriter = null!;

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
            CREATE TABLE dbo.[{_orders}] (Id INT NOT NULL PRIMARY KEY, Customer NVARCHAR(50) NOT NULL, Total INT NOT NULL);
            """);
        await ExecuteAsync(_adminConnection, $"ALTER TABLE dbo.[{_orders}] ENABLE CHANGE_TRACKING;");

        await ExecuteAsync(_adminConnection, $"""
            CREATE TABLE dbo.[{_lines}] (
                OrderId INT NOT NULL, LineNumber INT NOT NULL, Sku NVARCHAR(50) NOT NULL,
                CONSTRAINT [PK_{_lines}] PRIMARY KEY (OrderId, LineNumber));
            """);
        await ExecuteAsync(_adminConnection, $"ALTER TABLE dbo.[{_lines}] ENABLE CHANGE_TRACKING;");

        var secretStore = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var stateDatabase = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        var driverRegistry = new DriverRegistry();
        driverRegistry.Register(new MsSqlDriver());

        _configRepository = new ConfigRepository(
            Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secretStore);
        _taskRunStore = new TaskRunStore(stateDatabase);
        _workQueueStore = new WorkQueueStore(stateDatabase);
        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore,
            new LocalRunnerState(_taskRunStore, _workQueueStore, new RunLockStore(stateDatabase),
                new ChangeWatermarkStore(stateDatabase), new VerificationResultStore(stateDatabase),
                _logWriter = new LogWriter(stateDatabase)),
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

    /// <summary>
    /// The replication states no natural key at all — since phase 68 there is nowhere at this level to
    /// state one. Both mappings let the target table be created for them, which is also what proves
    /// provisioning extends the right mapping's writer Kind.
    /// </summary>
    private void SetUpConfig(WriterConfig? ordersWriterOverride = null)
    {
        ConnectionInput MakeConnectionInput(string name) => new()
        {
            DriverType = ConnectionDriverType.MsSql,
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
            Name = TaskName,
            Scheduling = new SchedulingConfig
            {
                Mode = ScheduleMode.Continuous, FrequencySeconds = 1, IdleTimeoutSeconds = 2,
            },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.ChangeTracking },
                Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                Writer = new WriterConfig { Kind = GenericDriverKinds.Scd2 },
            },
            Provisioning = new ProvisioningConfig { CreateTargetTableIfMissing = true },
        }, Author);

        SaveMapping("orders", _orders, _ordersTarget,
            [("Id", "Id"), ("Customer", "Customer"), ("Total", "Total")], ordersWriterOverride);

        SaveMapping("lines", _lines, _linesTarget,
            [("OrderId", "OrderId"), ("LineNumber", "LineNumber"), ("Sku", "Sku")], writerOverride: null);
    }

    private void SaveMapping(
        string name, string sourceTable, string targetTable, (string Source, string Target)[] columns,
        WriterConfig? writerOverride)
    {
        _configRepository.SaveTableMapping(TaskName, new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { ConnectionName = "src-conn", Database = _databaseName, Schema = "dbo", Table = sourceTable }],
            Targets = [new TableSpec { ConnectionName = "tgt-conn", Database = _databaseName, Schema = "dbo", Table = targetTable }],
            ColumnMappings = [.. columns.Select(c => new ColumnMapping { SourceColumn = c.Source, TargetColumn = c.Target })],
            WriterOverride = writerOverride,
        }, Author);
    }

    private async Task<TaskRunRecord> EnqueueAndDrainAsync(string mappingName)
    {
        var runId = _workQueueStore.Enqueue(TaskName, RunKind.Primary, mappingName);
        await _executor.ExecuteWorkerAsync(TaskName, degreeOfParallelism: 1, CancellationToken.None);
        return _taskRunStore.GetRun(runId)!;
    }

    /// <summary>
    /// A failed run's own message and its log, rather than "Succeeded != Failed". Worth the four lines:
    /// what these exercise is generated SQL running against a real server, and the statement that broke
    /// is in the log while the enum is not.
    /// </summary>
    private void AssertSucceeded(TaskRunRecord run) =>
        Assert.True(
            run.Status == RunStatus.Succeeded,
            $"{run.ErrorSummary}\n{string.Join("\n", _logWriter.GetLogs(run.RunId).Select(l => l.Message))}");

    private async Task<List<(string Version, string Key, string Value, bool Current)>> ReadVersionsAsync(
        string table, string keyExpression, string valueColumn)
    {
        await using var cmd = _adminConnection.CreateCommand();
        cmd.CommandText =
            $"SELECT {HistorizedColumns.SurrogateKey}, {keyExpression}, CAST({valueColumn} AS NVARCHAR(50)), " +
            $"{HistorizedColumns.IsCurrent} FROM dbo.[{table}];";

        var rows = new List<(string, string, string, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return rows;
    }

    /// <summary>
    /// The whole point of the phase. One replication, one SCD2 writer, two tables whose identities are
    /// nothing alike — <c>Id</c> and <c>(OrderId, LineNumber)</c> — and neither run has any way to know or
    /// care what the other's key is.
    /// </summary>
    [Fact]
    public async Task TwoMappingsUnderOneScd2Replication_EachVersionByItsOwnDerivedKey()
    {
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_orders}] (Id, Customer, Total) VALUES (1, 'Alice', 10), (2, 'Bob', 20);");
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_lines}] (OrderId, LineNumber, Sku) VALUES (1, 1, 'AAA'), (1, 2, 'BBB');");

        AssertSucceeded(await EnqueueAndDrainAsync("orders"));
        AssertSucceeded(await EnqueueAndDrainAsync("lines"));

        var orders = await ReadVersionsAsync(_ordersTarget, "CAST(Id AS NVARCHAR(50))", "Total");
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.True(o.Current));

        var lines = await ReadVersionsAsync(
            _linesTarget, "CONCAT(OrderId, '-', LineNumber)", "Sku");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Current));

        // Change one value in each table. A key that versioned correctly closes exactly the row whose
        // key changed and opens one new version for it — the two mappings each doing that against a
        // different key is the thing that was impossible before.
        await ExecuteAsync(_adminConnection, $"UPDATE dbo.[{_orders}] SET Total = 99 WHERE Id = 1;");
        await ExecuteAsync(_adminConnection, $"UPDATE dbo.[{_lines}] SET Sku = 'ZZZ' WHERE OrderId = 1 AND LineNumber = 2;");

        AssertSucceeded(await EnqueueAndDrainAsync("orders"));
        AssertSucceeded(await EnqueueAndDrainAsync("lines"));

        orders = await ReadVersionsAsync(_ordersTarget, "CAST(Id AS NVARCHAR(50))", "Total");
        Assert.Equal(3, orders.Count);
        Assert.Equal(["10"], orders.Where(o => o.Key == "1" && !o.Current).Select(o => o.Value));
        Assert.Equal(["99"], orders.Where(o => o.Key == "1" && o.Current).Select(o => o.Value));
        // Order 2 was untouched, so it has exactly one version and it is still open.
        Assert.Equal(["20"], orders.Where(o => o.Key == "2").Select(o => o.Value));

        lines = await ReadVersionsAsync(_linesTarget, "CONCAT(OrderId, '-', LineNumber)", "Sku");
        Assert.Equal(3, lines.Count);
        Assert.Equal(["BBB"], lines.Where(l => l.Key == "1-2" && !l.Current).Select(l => l.Value));
        Assert.Equal(["ZZZ"], lines.Where(l => l.Key == "1-2" && l.Current).Select(l => l.Value));
        // The other line's identity differs only in LineNumber, so a key derived as OrderId alone would
        // have versioned these two together. It has one version, untouched.
        Assert.Equal(["AAA"], lines.Where(l => l.Key == "1-1").Select(l => l.Value));
    }

    /// <summary>
    /// A stated key wins over the derived one. Named as a column this mapping does not write, so the
    /// writer's own "a key column has to be one this mapping writes" check is what fails — which it
    /// could only do if the override's value, and not the perfectly good derivable <c>Id</c>, is what
    /// reached the writer.
    /// </summary>
    [Fact]
    public async Task AMappingsExplicitNaturalKey_IsUsedInsteadOfTheDerivableOne()
    {
        SetUpConfig(ordersWriterOverride: new WriterConfig
        {
            Kind = GenericDriverKinds.Scd2,
            Options = { [Scd2Writer.NaturalKeyOption] = "NotAMappedColumn" },
        });

        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_orders}] (Id, Customer, Total) VALUES (1, 'Alice', 10);");

        var run = await EnqueueAndDrainAsync("orders");

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("NotAMappedColumn", run.ErrorSummary);
    }

    /// <summary>
    /// And a stated key that names real columns versions by those, rather than by the primary key that
    /// would otherwise have been derived.
    /// </summary>
    [Fact]
    public async Task AMappingsExplicitNaturalKey_VersionsByTheColumnsItNames()
    {
        SetUpConfig(ordersWriterOverride: new WriterConfig
        {
            Kind = GenericDriverKinds.Scd2,
            Options = { [Scd2Writer.NaturalKeyOption] = "Customer" },
        });

        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_orders}] (Id, Customer, Total) VALUES (1, 'Alice', 10);");
        AssertSucceeded(await EnqueueAndDrainAsync("orders"));

        // A second Alice, with a different Id. Under the stated key these are one dimension member
        // whose values changed — the first version closes and a second opens. Under the key that would
        // have been derived they are two members, and both would be current.
        await ExecuteAsync(_adminConnection,
            $"INSERT INTO dbo.[{_orders}] (Id, Customer, Total) VALUES (2, 'Alice', 20);");
        AssertSucceeded(await EnqueueAndDrainAsync("orders"));

        var rows = await ReadVersionsAsync(_ordersTarget, "Customer", "Total");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Alice", r.Key));
        Assert.Equal(["10"], rows.Where(r => !r.Current).Select(r => r.Value));
        Assert.Equal(["20"], rows.Where(r => r.Current).Select(r => r.Value));
    }
}
