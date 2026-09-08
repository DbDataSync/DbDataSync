using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.MsSql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// What the polling gate writes down on its own tick, against a real server — specifically that a
/// Change Tracking group now comes back with the engine's own time for the version it just fetched,
/// not only with the version (phase 87).
/// <para>
/// **This is the half of reader lag that has to be captured here or nowhere.** Phase 85 filled this
/// in for CDC alone, on the reasoning that <c>fn_cdc_map_lsn_to_time</c> rides along on the
/// round-trip that already fetched the LSN while <c>dm_tran_commit_table</c> is a query of its own,
/// "better made on demand". On demand turned out to mean once per mapping per thirty-second refresh
/// of every status screen watching the source. One query per group per tick is strictly fewer, and it
/// is the moment the version is certain to still be inside that DMV's rolling window.
/// </para>
/// <para>
/// Against a real server because there is nothing to fake: the claim is about what SQL Server itself
/// will say about a version, on a connection pointed at the right database.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChangeCounterSourceIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly string _databaseName = $"DbDataSyncCounterTest_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"counter-src-{Guid.NewGuid():N}";

    public ChangeCounterSourceIntegrationTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON "
                    + "(CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        await using var db = new SqlConnection(builder.ConnectionString);
        await db.OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE dbo.[Orders] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50));");
        await ExecuteAsync(db, "ALTER TABLE dbo.[Orders] ENABLE CHANGE_TRACKING;");

        // A committed write, so the database's current version is one the DMV has a commit time for.
        // A version nothing has ever reached maps to nothing, and would prove only that null is null.
        await ExecuteAsync(db, "INSERT INTO dbo.[Orders] (Id, Name) VALUES (1, 'one');");

        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), "DbDataSync_Test_Pw1");

        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(_connectionName)), null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    [Fact]
    public async Task AChangeTrackingTick_RecordsTheEnginesTimeForTheVersionItFetched()
    {
        var counters = _factory.Services.GetRequiredService<IChangeCounterSource>();

        var reading = await counters.FetchAsync(
            _connectionName, _databaseName, MsSqlDriverKinds.ChangeTracking, CancellationToken.None);

        // The version is what phase 75 already fetched; the time beside it is the whole of phase 87's
        // change to this branch, and the reason ReaderLagService needs no connection to a source.
        Assert.False(string.IsNullOrEmpty(reading.Value));
        Assert.NotNull(reading.SourceTimeUtc);

        // Paired with the version it came from, not with whatever the DMV last held: mapping the
        // returned version independently has to land on the same instant.
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        await using var db = new SqlConnection(builder.ConnectionString);
        await db.OpenAsync();

        Assert.Equal(
            await MsSqlChangeTrackingReader.MapVersionToTimeAsync(
                db, long.Parse(reading.Value!), CancellationToken.None),
            reading.SourceTimeUtc);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
