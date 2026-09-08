using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The Setup card's Apply button, and the cache it never used to fill — phase 97.
/// <para>
/// Two paths create a target table and phase 94 taught only one of them to record what it created. A
/// mapping provisioned by hand — describe it, preview the DDL, press Apply — got a correct table and an
/// empty column cache, and then failed its own first run under phase 91's
/// <c>MetadataNotCachedException</c>, needing the manual Refresh that phase 94 existed to remove. These
/// tests press Apply and read the mapping back; **no <c>refresh-metadata</c> call appears anywhere
/// below**, and that absence is the assertion.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApplyProvisioningCacheIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client;

    private readonly string _sourceDb = $"DbDataSyncApplySrc_{Guid.NewGuid():N}";
    private readonly string _targetDb = $"DbDataSyncApplyTgt_{Guid.NewGuid():N}";
    private readonly string _srcConnectionName = $"applysrc-{Guid.NewGuid():N}";
    private readonly string _tgtConnectionName = $"applytgt-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"apply-{Guid.NewGuid():N}";

    /// <summary>The source both mappings read: two columns, one of them the key.</summary>
    private const string SourceTable = "Orders";

    public ApplyProvisioningCacheIntegrationTests(TestApiFactory factory) => _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            foreach (var db in new[] { _sourceDb, _targetDb })
                await ExecuteAsync(bootstrap, $"CREATE DATABASE [{db}];");
        }

        await using (var source = await OpenAsync(_sourceDb))
        {
            await ExecuteAsync(source,
                $"CREATE TABLE dbo.[{SourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        }

        await CreateConnectionAsync(_srcConnectionName, _sourceDb);
        await CreateConnectionAsync(_tgtConnectionName, _targetDb);
        await CreateReplicationAsync();
    }

    public async Task DisposeAsync()
    {
        // The API under test keeps pooled connections to these scratch databases. SET SINGLE_USER
        // kicks its live sessions off, but a pooled one can reconnect into the freed single-user slot
        // before DROP runs — "database is currently in use". Emptying the pools first stops that, and
        // a short retry covers the window that remains.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            await ExecuteAsync(connection, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await ExecuteAsync(connection, $"DROP DATABASE [{db}];");
                    break;
                }
                catch (SqlException) when (attempt < 5)
                {
                    SqlConnection.ClearAllPools();
                    await Task.Delay(500);
                }
            }
        }
    }

    /// <summary>
    /// **The gap phase 97 closes.** A target that does not exist has nothing to capture when the
    /// mapping is saved, so the cache is empty; Apply creates the table and — now — records the shape
    /// it ended up with.
    /// <para>
    /// The stored columns are compared against the target's own catalog rather than against a list
    /// written here, because that equality is the whole reason this reads the table back instead of
    /// trusting the plan: the plan knows canonical types and no identity flags, and the cache's
    /// consumers build DDL from the native type strings only the catalog can give.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Apply_CreatingTheTarget_CachesTheShapeItCreated()
    {
        const string target = "CreatedByApply";
        await SaveMappingAsync("created", target);

        // The premise: nothing was captured at save time, because there was no table to read.
        Assert.Empty((await LoadMappingAsync("created")).TargetColumns);

        await ApplyAsync("created", ProvisioningActions.CreateTargetTable);

        var mapping = await LoadMappingAsync("created");
        Assert.Equal(["Id", "Name"], mapping.TargetColumns.Select(c => c.Name));
        Assert.NotNull(mapping.ColumnsCapturedUtc);
        Assert.Equal(
            await CatalogOfAsync(target), mapping.TargetColumns, SameFlags);

        // The native types the target rendered, which is the reason this reads the catalog back at all:
        // the plan carried canonical types, and staging builds its own DDL from these strings.
        Assert.Equal(["int", "nvarchar(50)"], mapping.TargetColumns.Select(c => c.NativeType));
    }

    /// <summary>
    /// The alter path, on the same terms. A cache describing the table as it was before the ALTER is a
    /// cache missing the column Apply just added — and unlike the create case it is a *wrong* answer
    /// rather than an absent one, which phase 91 has no way to notice.
    /// </summary>
    [Fact]
    public async Task Apply_AlteringTheTarget_CachesTheColumnItAdded()
    {
        const string target = "AlteredByApply";
        await using (var connection = await OpenAsync(_targetDb))
            await ExecuteAsync(connection, $"CREATE TABLE dbo.[{target}] (Id INT NOT NULL PRIMARY KEY);");

        // The opposite premise to the create case: this mapping's cache is populated and *correct* —
        // the table it describes really does have one column — which is the state a mapping is in when
        // an operator adds a column mapping to it and goes to the Setup card to apply the ALTER.
        await SaveMappingAsync("altered", target,
            targetColumns: [new CachedColumn("Id", "int", false, true, false)]);

        Assert.Equal(["Id"], (await LoadMappingAsync("altered")).TargetColumns.Select(c => c.Name));

        await ApplyAsync("altered", ProvisioningActions.AlterTargetTable);

        var mapping = await LoadMappingAsync("altered");
        Assert.Equal(["Id", "Name"], mapping.TargetColumns.Select(c => c.Name));
        Assert.Equal(
            await CatalogOfAsync(target), mapping.TargetColumns, SameFlags);
    }

    /// <summary>
    /// Apply on a target that is already right writes nothing. The cache is compared before it is
    /// saved, so a second press — much the commonest way this endpoint is called twice — leaves the
    /// capture timestamp where it was rather than restamping a picture nobody retook.
    /// </summary>
    [Fact]
    public async Task Apply_OnATargetAlreadyInShape_DoesNotRestampTheCapture()
    {
        const string target = "AlreadyRight";
        await SaveMappingAsync("again", target);
        await ApplyAsync("again", ProvisioningActions.CreateTargetTable);

        var captured = (await LoadMappingAsync("again")).ColumnsCapturedUtc;
        Assert.NotNull(captured);

        await ApplyAsync("again", ProvisioningActions.CreateTargetTable);

        var mapping = await LoadMappingAsync("again");
        Assert.Equal(captured, mapping.ColumnsCapturedUtc);
        Assert.Equal(["Id", "Name"], mapping.TargetColumns.Select(c => c.Name));
    }

    /// <summary>
    /// Name, nullability, key and identity — everything <see cref="CatalogOfAsync"/> reads for itself.
    /// The native type is asserted separately, against the literal SQL Server renders, because
    /// reproducing that rendering here would be a second implementation of the thing under test.
    /// </summary>
    private static bool SameFlags(CachedColumn a, CachedColumn b) =>
        a.Name == b.Name
        && a.IsNullable == b.IsNullable
        && a.IsPrimaryKey == b.IsPrimaryKey
        && a.IsIdentity == b.IsIdentity;

    private async Task ApplyAsync(string mappingName, string action)
    {
        var response = await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/{mappingName}/provisioning/{action}/apply", null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(ProvisioningState.Satisfied), result.GetProperty("state").GetString());
    }

    private async Task<TableMappingConfig> LoadMappingAsync(string mappingName) =>
        (await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{_replicationName}/table-mappings/{mappingName}", JsonOptions))!;

    /// <param name="targetColumns">The cache the mapping is saved with. Left empty for a target that
    /// does not exist yet, which is what a mapping described through this endpoint genuinely has: a
    /// plain save preserves a stored capture but never introspects for one (see
    /// <c>MappingMetadataCapture</c>), so an empty list here is the real starting state and not a
    /// shortcut.</param>
    private async Task SaveMappingAsync(
        string mappingName, string targetTable, List<CachedColumn>? targetColumns = null) =>
        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/{mappingName}", new TableMappingConfig
            {
                Name = mappingName,
                TargetColumns = targetColumns ?? [],
                Sources = [new SourceTableSpec { ConnectionName = _srcConnectionName, Database = _sourceDb, Table = SourceTable }],
                Targets = [new TableSpec { ConnectionName = _tgtConnectionName, Database = _targetDb, Table = targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
                ],
            }, JsonOptions)).EnsureSuccessStatusCode();

    /// <summary>One target table's shape as the cache stores it, read straight from SQL Server — the
    /// independent answer these tests compare the cache against.</summary>
    private async Task<List<CachedColumn>> CatalogOfAsync(string table)
    {
        await using var connection = await OpenAsync(_targetDb);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, c.is_nullable, c.is_identity,
                   CONVERT(bit, CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic
                ON ic.object_id = t.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            WHERE t.name = @table
            ORDER BY c.column_id;
            """;
        cmd.Parameters.AddWithValue("@table", table);

        var columns = new List<CachedColumn>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // NativeType is left as the cache's own answer: what this comparison is for is the name,
            // nullability, key and identity flags, and rebuilding SQL Server's type rendering here
            // would be a second implementation of the thing under test.
            columns.Add(new CachedColumn(reader.GetString(0), "", reader.GetBoolean(1), reader.GetBoolean(3), reader.GetBoolean(2)));
        }

        return columns;
    }

    private async Task<SqlConnection> OpenAsync(string database)
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateConnectionAsync(string name, string database) =>
        (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = database,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateReplicationAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            // Disabled: these tests only exercise the Apply endpoint and never trigger a run, so a
            // scheduled worker spawning against the scratch databases is pure noise — and a worker
            // still holding a connection when DisposeAsync runs DROP DATABASE is what surfaced as an
            // intermittent "database is currently in use" teardown failure.
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
}
