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
/// shape, and it's ~30 lines — see architecture/implementation/phase-3-mssql-driver.md's notes on
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

    private ConfigRepository _configRepository = null!;
    private RunExecutor _executor = null!;
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

        var secretStore = SecretStore.ForProviders([new InMemorySecretProvider()]);
        var stateDatabase = new StateDatabase(Path.Combine(_repoRoot, "state.db"));
        var driverRegistry = new DriverRegistry();
        driverRegistry.Register(new MsSqlDriver());

        _configRepository = new ConfigRepository(Path.Combine(_repoRoot, "config"), new GitCommitService(_repoRoot), secretStore);
        _executor = new RunExecutor(
            _configRepository, driverRegistry, secretStore, new TaskRunStore(stateDatabase),
            new ChangeWatermarkStore(stateDatabase), new RunLockStore(stateDatabase), new LogWriter(stateDatabase));

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

    [Fact]
    public async Task FullPipeline_FullLoadThenIncrementalRun_ReplicatesAndRecordsState()
    {
        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");

        var firstRunId = Guid.NewGuid();
        var firstResult = await _executor.ExecuteAsync("e2e-sync", firstRunId, CancellationToken.None);

        Assert.Equal(ExitCode.Success, firstResult);
        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, await GetTargetRowsAsync());

        await ExecuteAsync(_adminConnection, $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (3, 'Carol');");
        await ExecuteAsync(_adminConnection, $"UPDATE dbo.[{_sourceTable}] SET Name = 'Robert' WHERE Id = 2;");
        await ExecuteAsync(_adminConnection, $"DELETE FROM dbo.[{_sourceTable}] WHERE Id = 1;");

        var secondRunId = Guid.NewGuid();
        var secondResult = await _executor.ExecuteAsync("e2e-sync", secondRunId, CancellationToken.None);

        Assert.Equal(ExitCode.Success, secondResult);
        Assert.Equal(new Dictionary<int, string> { [2] = "Robert", [3] = "Carol" }, await GetTargetRowsAsync());
    }
}
