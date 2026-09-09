using System.Data.Common;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Libraries;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// **The milestone phase 109d exists for**: MySQL — an engine no project in this solution
/// references — replicating into SQL Server, driven entirely through the real API and a real spawned
/// TaskRunner process, with the MySQL side coming from nothing but a restored library and a
/// <c>driver.yaml</c> <see cref="DescriptorDriverApiFactory"/> wrote to disk before the host started.
/// "First new engine, no rebuild" is the plan doc's own words for this test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DescriptorDriverTests : IClassFixture<DescriptorDriverApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string MsSqlServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private static string MySqlServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MYSQL_SERVER")
        ?? "Host=localhost;Port=13306;User Id=root;Password=DbDataSync_Test_Pw1;Connect Timeout=30";

    private readonly DescriptorDriverApiFactory _factory;
    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly DbProviderFactory _mySqlFactory;

    private readonly string _mySqlDatabase = $"dbdatasync_desc_test_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetDatabase = $"DbDataSyncDescTest_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"mysql-src-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"mssql-tgt-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"repl-desc-{Guid.NewGuid():N}";

    public DescriptorDriverTests(DescriptorDriverApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // first touch — builds the host, which is when both
                                           // LibraryRegistry and DriverRegistry (via DriverLoader) load.
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        // Resolved through the same registry the API itself loaded at startup — proof this test seeds
        // its scratch database through the identical runtime-loaded factory the driver uses, not a
        // MySqlConnector type this project references directly (it references none).
        _mySqlFactory = factory.Services.GetRequiredService<LibraryRegistry>().GetFactory("MySqlConnector");
    }

    public async Task InitializeAsync()
    {
        await using (var admin = OpenMySql(""))
        {
            await ExecAsync(admin, $"CREATE DATABASE `{_mySqlDatabase}`;");
        }

        await using (var db = OpenMySql(_mySqlDatabase))
        {
            await ExecAsync(db, $"""
                CREATE TABLE `{_sourceTable}` (
                    id INT NOT NULL PRIMARY KEY,
                    name VARCHAR(100) NOT NULL,
                    modified_at DATETIME NOT NULL);
                """);
            await ExecAsync(db, $"""
                INSERT INTO `{_sourceTable}` (id, name, modified_at) VALUES
                    (1, 'Alice', '2026-01-01 09:00:00'),
                    (2, 'Bob', '2026-01-02 09:00:00');
                """);
        }

        await using (var bootstrap = new SqlConnection(MsSqlServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecAsync(bootstrap, $"CREATE DATABASE [{_targetDatabase}];");
        }

        var tgtBuilder = new SqlConnectionStringBuilder(MsSqlServerConnectionString) { InitialCatalog = _targetDatabase };
        await using var tgtConnection = new SqlConnection(tgtBuilder.ConnectionString);
        await tgtConnection.OpenAsync();
        await ExecAsync(tgtConnection, $"CREATE TABLE dbo.[{_targetTable}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(100) NOT NULL, modified_at DATETIME2(0) NOT NULL);");

        // Same reason RunLifecycleIntegrationTests sets this: the spawned TaskRunner is a real child
        // process with its own SecretStore, not this test host's in-memory one.
        SetSecretEnvVar(_srcConnectionName, "DbDataSync_Test_Pw1");
        SetSecretEnvVar(_tgtConnectionName, "DbDataSync_Test_Pw1");

        await CreateMySqlConnectionAsync();
        await CreateMsSqlConnectionAsync();
        await CreateReplicationAsync();
    }

    public async Task DisposeAsync()
    {
        ClearSecretEnvVar(_srcConnectionName);
        ClearSecretEnvVar(_tgtConnectionName);

        await using (var admin = OpenMySql(""))
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS `{_mySqlDatabase}`;");

        await using var mssql = new SqlConnection(MsSqlServerConnectionString);
        await mssql.OpenAsync();
        await ExecAsync(mssql, $"ALTER DATABASE [{_targetDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecAsync(mssql, $"DROP DATABASE [{_targetDatabase}];");
    }

    private DbConnection OpenMySql(string database)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = MySqlServerConnectionString };
        if (!string.IsNullOrEmpty(database))
            builder["Database"] = database;

        var connection = _mySqlFactory.CreateConnection()!;
        connection.ConnectionString = builder.ConnectionString;
        connection.Open();
        return connection;
    }

    private void SetSecretEnvVar(string connectionName, string password) =>
        Environment.SetEnvironmentVariable(SecretEnvVarName(connectionName), password);

    private void ClearSecretEnvVar(string connectionName) =>
        Environment.SetEnvironmentVariable(SecretEnvVarName(connectionName), null);

    private string SecretEnvVarName(string connectionName) =>
        _secrets.EnvName(SecretRefs.ForConnection(connectionName));

    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateMySqlConnectionAsync() =>
        (await _client.PutAsJsonAsync($"/api/connections/{_srcConnectionName}", new ConnectionInput
        {
            Name = _srcConnectionName,
            DriverType = DescriptorDriverApiFactory.DriverId,
            Host = "localhost",
            Port = 13306,
            Database = _mySqlDatabase,
            AuthMode = AuthMode.SqlAuth,
            UserId = "root",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateMsSqlConnectionAsync() =>
        (await _client.PutAsJsonAsync($"/api/connections/{_tgtConnectionName}", new ConnectionInput
        {
            Name = _tgtConnectionName,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _targetDatabase,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateReplicationAsync()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = true,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                // "Watermark" is a GenericDriverKinds name, not an MsSql-prefixed one — resolved from
                // the mysql.generic driver's own registered Readers, exactly as an MsSql-prefixed Kind
                // resolves from MsSqlDriver's.
                Reader = new ReaderConfig { Kind = "Watermark", Options = { ["watermarkColumn"] = "modified_at" } },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlDeleteInsert" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
        {
            Name = "main",
            Sources = [new SourceTableSpec { ConnectionName = _srcConnectionName, Database = _mySqlDatabase, Schema = _mySqlDatabase, Table = _sourceTable }],
            Targets = [new TableSpec { ConnectionName = _tgtConnectionName, Database = _targetDatabase, Schema = "dbo", Table = _targetTable }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "id", TargetColumn = "id" },
                new ColumnMapping { SourceColumn = "name", TargetColumn = "name" },
                new ColumnMapping { SourceColumn = "modified_at", TargetColumn = "modified_at" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // As RunLifecycleIntegrationTests: the PUT above saves the mapping, it does not introspect —
        // this is the server-side column-metadata capture the reader/staging/writer need cached.
        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DriversEndpoint_ListsTheDescriptorDriver()
    {
        var drivers = await _client.GetFromJsonAsync<JsonElement[]>("/api/drivers");

        var mysqlGeneric = drivers!.Single(d => d.GetProperty("id").GetString() == DescriptorDriverApiFactory.DriverId);
        Assert.Equal("MySQL / MariaDB (generic)", mysqlGeneric.GetProperty("displayName").GetString());
        Assert.False(mysqlGeneric.GetProperty("builtIn").GetBoolean());
        Assert.Equal("descriptor", mysqlGeneric.GetProperty("source").GetString());

        // The three built-ins are still there too — a descriptor driver is additive, not a replacement.
        Assert.Contains(drivers!, d => d.GetProperty("id").GetString() == DriverIds.MsSql && d.GetProperty("builtIn").GetBoolean());
    }

    [Fact]
    public async Task MySqlToSqlServer_WatermarkReplication_LandsBothRows()
    {
        await using var hubConnection = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}hubs/run", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        JsonElement? completedPayload = null;
        var completedTcs = new TaskCompletionSource();
        hubConnection.On<JsonElement>("runCompleted", payload =>
        {
            completedPayload = payload;
            completedTcs.TrySetResult();
        });
        await hubConnection.StartAsync();

        var triggerResponse = await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        triggerResponse.EnsureSuccessStatusCode();
        var triggerBody = await triggerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = triggerBody.GetProperty("runIds").EnumerateArray().Single().GetString();

        await hubConnection.InvokeAsync("JoinRun", runId);
        var completedTask = await Task.WhenAny(completedTcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(completedTcs.Task, completedTask);

        Assert.NotNull(completedPayload);
        Assert.Equal("Succeeded", completedPayload!.Value.GetProperty("status").GetString());
        Assert.Equal(2, completedPayload.Value.GetProperty("rowsRead").GetInt64());
        Assert.Equal(2, completedPayload.Value.GetProperty("rowsWritten").GetInt64());

        var tgtBuilder = new SqlConnectionStringBuilder(MsSqlServerConnectionString) { InitialCatalog = _targetDatabase };
        await using var tgtConnection = new SqlConnection(tgtBuilder.ConnectionString);
        await tgtConnection.OpenAsync();
        await using var cmd = tgtConnection.CreateCommand();
        cmd.CommandText = $"SELECT id, name FROM dbo.[{_targetTable}] ORDER BY id;";
        var landed = new Dictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            landed[reader.GetInt32(0)] = reader.GetString(1);

        Assert.Equal(new Dictionary<int, string> { [1] = "Alice", [2] = "Bob" }, landed);
    }
}
