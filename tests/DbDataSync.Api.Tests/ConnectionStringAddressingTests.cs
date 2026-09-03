using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A connection expressed as an engine-native connection string, end to end — saved, round-tripped, and
/// proved reachable through phase 19's own test endpoint.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectionStringAddressingTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Report(bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error);

    private async Task<HttpResponseMessage> SaveAsync(string name, ConnectionInput input) =>
        await _client.PutAsJsonAsync($"/api/connections/{name}", input, JsonOptions);

    [Fact]
    public async Task AConnectionStringConnection_SavesRoundTripsAndIsReachable()
    {
        var name = $"cs-{Guid.NewGuid():N}";

        // No host, no port — everything the engine needs is in the string, except the credential, which
        // is deliberately not allowed in it.
        var response = await SaveAsync(name, new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            ConnectionString = "Server=localhost,14330;TrustServerCertificate=True",
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        });
        response.EnsureSuccessStatusCode();

        var saved = await _client.GetFromJsonAsync<ConnectionConfig>($"/api/connections/{name}", JsonOptions);
        Assert.Null(saved!.Host);
        Assert.Contains("Server=localhost,14330", saved.ConnectionString);
        // The credential went to the secret store, not into the file that gets committed.
        Assert.DoesNotContain("DbDataSync_Test_Pw1", saved.ConnectionString);

        var test = await _client.PostAsync($"/api/connections/{name}/test", null);
        test.EnsureSuccessStatusCode();
        var report = await test.Content.ReadFromJsonAsync<Report>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.Contains("SQL Server", report.ServerVersion);
    }

    [Fact]
    public async Task TheCredentialIsAppliedOnTopOfTheOperatorsString()
    {
        // The string carries no credential at all; the connection only works because the driver splices
        // the resolved one in through the provider's own builder at connect time.
        var name = $"cs-{Guid.NewGuid():N}";
        (await SaveAsync(name, new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            ConnectionString = "Server=localhost,14330;TrustServerCertificate=True;Application Name=DbDataSyncTest",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        })).EnsureSuccessStatusCode();

        var report = await (await _client.PostAsync($"/api/connections/{name}/test", null))
            .Content.ReadFromJsonAsync<Report>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
    }

    [Fact]
    public async Task AConnectionStringCarryingACredential_IsRejectedAtSave()
    {
        var name = $"cs-{Guid.NewGuid():N}";

        var response = await SaveAsync(name, new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            ConnectionString = "Server=localhost,14330;User Id=sa;Password=DbDataSync_Test_Pw1",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secret store", await response.Content.ReadAsStringAsync());

        // And nothing was written on the way to being rejected — the addressing check runs before any
        // side effect, which it did not when it was first added.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/connections/{name}")).StatusCode);
    }

    [Fact]
    public async Task AConnectionWithNeitherAddress_IsRejected()
    {
        var name = $"cs-{Guid.NewGuid():N}";

        var response = await SaveAsync(name, new ConnectionInput
        {
            Name = name, DriverType = ConnectionDriverType.MsSql, AuthMode = AuthMode.SqlAuth, UserId = "sa",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AHostConnectionWrittenBeforeThisPhase_StillLoadsAndWorks()
    {
        // The whole reason the fields are flat and nullable: every connection already on disk has a host
        // and no connection string, which reads back as host mode with no migration.
        var name = $"host-{Guid.NewGuid():N}";
        (await SaveAsync(name, new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        })).EnsureSuccessStatusCode();

        var saved = await _client.GetFromJsonAsync<ConnectionConfig>($"/api/connections/{name}", JsonOptions);
        Assert.Equal("localhost", saved!.Host);
        Assert.Null(saved.ConnectionString);

        var report = await (await _client.PostAsync($"/api/connections/{name}/test", null))
            .Content.ReadFromJsonAsync<Report>(JsonOptions);
        Assert.True(report!.Succeeded, report.Error);
    }

    [Fact]
    public async Task Postgres_TakesAConnectionStringToo()
    {
        var name = $"pg-{Guid.NewGuid():N}";
        (await SaveAsync(name, new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.Postgres,
            ConnectionString = "Host=localhost;Port=15432;Timeout=5",
            Database = "postgres",
            AuthMode = AuthMode.SqlAuth,
            UserId = "dbdatasync",
            Password = "DbDataSync_Test_Pw1",
        })).EnsureSuccessStatusCode();

        var report = await (await _client.PostAsync($"/api/connections/{name}/test", null))
            .Content.ReadFromJsonAsync<Report>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.Contains("PostgreSQL", report.ServerVersion);
    }
}
