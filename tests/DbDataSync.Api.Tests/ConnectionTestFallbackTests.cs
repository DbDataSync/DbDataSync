using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The bug a real report found: a connection's own test query never ran when the driver's *generic*
/// reachability probe (<c>GenericDriverBase.TestAsync</c>'s hardcoded <c>SELECT 1</c>) failed — because
/// <c>ConnectionsController.Test</c> only attempted the configured test query once that generic probe
/// had already succeeded. That's backwards: a custom test query exists precisely to prove a connection
/// works on an engine whose generic probe cannot (one that requires every SELECT to name a table, say),
/// so gating it on that same probe's success made the feature unusable for exactly the connections it
/// was built for.
/// <para>
/// No real engine in this test environment actually rejects a bare <c>SELECT 1</c>, so this simulates
/// one: a driver registered directly into the running host's own DriverRegistry (bypassing a
/// driver.yaml entirely) whose <see cref="IConnectionTester.TestAsync"/> always reports failure, while
/// its <see cref="IDriver.CreateConnection"/> hands back a perfectly real, working connection to the
/// Postgres test container.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectionTestFallbackTests : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    private readonly HttpClient _client;
    private readonly SecretStore _secrets;

    public ConnectionTestFallbackTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        factory.Services.GetRequiredService<DriverRegistry>().Register(new AlwaysFailingProbeDriver());
    }

    [Fact]
    public async Task Test_WhenTheGenericProbeFailsButTheTestQuerySucceeds_ReportsOverallSuccess()
    {
        var name = $"fallback-probe-conn-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(_secrets.EnvName(SecretRefs.ForConnection(name)), "DbDataSync_Test_Pw1");
        try
        {
            (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
            {
                Name = name,
                DriverType = AlwaysFailingProbeDriver.Id,
                ConnectionString = "Host=localhost;Port=15432;Database=postgres",
                AuthMode = AuthMode.SqlAuth,
                UserId = "dbdatasync",
                Password = "DbDataSync_Test_Pw1",
                TestQuery = "SELECT current_database() AS db",
            }, JsonOptions)).EnsureSuccessStatusCode();

            var response = await _client.PostAsync($"/api/connections/{name}/test", null);
            response.EnsureSuccessStatusCode();
            var report = await response.Content.ReadFromJsonAsync<JsonElement>();

            // The generic probe really did fail — this isn't succeeding by accident because the fake
            // driver secretly reports success.
            Assert.True(report.GetProperty("succeeded").GetBoolean());
            var testQueryResult = report.GetProperty("testQueryResult");
            Assert.True(testQueryResult.ValueKind != JsonValueKind.Null);
            Assert.Null(testQueryResult.GetProperty("error").GetString());
            Assert.Equal("db", testQueryResult.GetProperty("columns")[0].GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(_secrets.EnvName(SecretRefs.ForConnection(name)), null);
        }
    }

    [Fact]
    public async Task Test_WhenBothTheGenericProbeAndTheTestQueryFail_ReportsFailure()
    {
        var name = $"fallback-probe-conn-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(_secrets.EnvName(SecretRefs.ForConnection(name)), "DbDataSync_Test_Pw1");
        try
        {
            (await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
            {
                Name = name,
                DriverType = AlwaysFailingProbeDriver.Id,
                ConnectionString = "Host=localhost;Port=15432;Database=postgres",
                AuthMode = AuthMode.SqlAuth,
                UserId = "dbdatasync",
                Password = "DbDataSync_Test_Pw1",
                TestQuery = "SELECT * FROM this_table_does_not_exist_anywhere",
            }, JsonOptions)).EnsureSuccessStatusCode();

            var response = await _client.PostAsync($"/api/connections/{name}/test", null);
            response.EnsureSuccessStatusCode();
            var report = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.False(report.GetProperty("succeeded").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("error").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(_secrets.EnvName(SecretRefs.ForConnection(name)), null);
        }
    }

    private sealed class AlwaysFailingProbeDriver : IDriver, IConnectionTester
    {
        public const string Id = "always-failing-probe";

        public string DriverType => Id;
        public IReadOnlyList<IChangeReader> Readers => [];
        public IReadOnlyList<IStagingProvider> StagingProviders => [];
        public IReadOnlyList<IChangeWriter> Writers => [];

        public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
        {
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
            {
                Username = connection.UserId,
                Password = credential,
            };
            return new NpgsqlConnection(builder.ConnectionString);
        }

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <summary>Simulates an engine whose generic probe cannot run at all (e.g. requires a table
        /// name in every SELECT) even though the connection itself is perfectly usable.</summary>
        public Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(
                false, TimeSpan.Zero, null, "simulated: this engine's generic probe always fails"));
    }
}
