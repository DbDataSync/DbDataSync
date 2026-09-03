using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Testing a connection end to end, against the real SQL Server the rest of the integration suite
/// uses. The case that matters most is the failing one: an unreachable database must come back as a
/// *result*, not as an exception or a 500.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectionTestIntegrationTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Report(bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error);
    private sealed record Source(string Store, string SecretRef, string EnvironmentVariable, bool RequiresCredential);

    private async Task<string> CreateConnectionAsync(int port)
    {
        var name = $"test-conn-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = port,
            Database = "master",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return name;
    }

    [Fact]
    public async Task Test_AgainstAReachableServer_ReportsSuccessWithAVersionAndATiming()
    {
        var name = await CreateConnectionAsync(14330);

        var response = await _client.PostAsync($"/api/connections/{name}/test", null);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<Report>(JsonOptions);

        Assert.True(report!.Succeeded, report.Error);
        Assert.Contains("SQL Server", report.ServerVersion);
        // One line, not @@VERSION's four — a status card, not a wall of build and OS detail.
        Assert.DoesNotContain('\n', report.ServerVersion!);
        Assert.True(report.ConnectMs > 0);
        Assert.Null(report.Error);
    }

    [Fact]
    public async Task Test_AgainstAClosedPort_ReportsFailureRatherThanThrowing()
    {
        // Port 1 is reserved and nothing listens on it: the failure happens while *opening*, which
        // never reaches the driver's probe — the path most likely to escape as a 500 if unhandled.
        var name = await CreateConnectionAsync(1);

        var response = await _client.PostAsync($"/api/connections/{name}/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<Report>(JsonOptions);
        Assert.False(report!.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(report.Error));
        Assert.Null(report.ServerVersion);
    }

    [Fact]
    public async Task Test_ForAnUnknownConnection_Is404()
    {
        var response = await _client.PostAsync($"/api/connections/nope-{Guid.NewGuid():N}/test", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CredentialSource_NamesTheExactEnvironmentVariableTheProcessLooksFor()
    {
        var name = await CreateConnectionAsync(14330);

        var source = await _client.GetFromJsonAsync<Source>($"/api/connections/{name}/credential-source", JsonOptions);

        Assert.Equal($"dbdatasync:connection:{name}", source!.SecretRef);
        // Phase 93: DBDATASYNC_SECRET_* now, not the package's unconfigured "ClrKernel" default — the
        // repeated "DBDATASYNC" is inherent to the two prefixes (SecretStore's provider-naming prefix
        // and SecretRefs' own "dbdatasync:" ref-namespacing) serving different purposes, not a bug.
        Assert.Equal(
            $"DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_{name.Replace('-', '_').ToUpperInvariant()}",
            source.EnvironmentVariable);
        Assert.True(source.RequiresCredential);
    }
}
