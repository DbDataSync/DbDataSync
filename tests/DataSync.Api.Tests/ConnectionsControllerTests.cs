using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using LibGit2Sharp;
using Xunit;

namespace DataSync.Api.Tests;

public sealed class ConnectionsControllerTests : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;

    public ConnectionsControllerTests(TestApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static ConnectionInput MakeInput(string name) => new()
    {
        Name = name,
        DriverType = ConnectionDriverType.MsSql,
        Host = "sql01",
        Port = 1433,
        Database = "App",
        AuthMode = AuthMode.SqlAuth,
        UserId = "svc_app",
        Password = "sup3r-s3cr3t",
    };

    /// <summary>
    /// The endpoint a UI builds its reader/cache/writer pickers from. What matters is that every entry
    /// comes from the registered driver's own declarations — so this asserts the capability flags
    /// track the writers' SupportsReconciliation and the readers' ISegmentExpandingReader
    /// implementation, not that a particular hardcoded list came back.
    /// </summary>
    [Fact]
    public async Task Capabilities_ReportsWhatTheRegisteredDriverActuallySupports()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions)).EnsureSuccessStatusCode();

        var response = await _client.GetAsync($"/api/connections/{name}/capabilities");
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<DriverCapabilities>(JsonOptions);

        Assert.NotNull(capabilities);
        Assert.Equal(ConnectionDriverType.MsSql, capabilities!.DriverType);

        // Only the reload readers can expand an Auto segment into concrete ranges — this driver's own
        // and the portable one it registers alongside it.
        Assert.Equal(
            ["BatchReload", "MsSqlBatchReload"],
            capabilities.Readers.Where(r => r.SupportsSegmentation).Select(r => r.Kind).Order());

        // The change-feed readers report a source delete as one; the scanning readers cannot see a
        // row that is no longer there. TriggerAudit is the engine-neutral one, which is the point of
        // it — it brings delete detection to every engine with triggers rather than one.
        Assert.Equal(
            ["MsSqlCdc", "MsSqlChangeTracking", "TriggerAudit"],
            capabilities.Readers.Where(r => r.DetectsDeletes).Select(r => r.Kind).Order());

        // The SPA decides whether to offer a Test action from this flag alone.
        Assert.True(capabilities.SupportsConnectionTest);

        // The reload writers reconcile; the incremental MERGE writer is upsert-only.
        Assert.Equal(
            ["DeleteInsert", "MsSqlDeleteInsert", "MsSqlMergeReconcile"],
            capabilities.Writers.Where(w => w.SupportsReconciliation).Select(w => w.Kind).Order());
        Assert.False(capabilities.Writers.Single(w => w.Kind == "MsSqlMerge").SupportsReconciliation);

        Assert.NotEmpty(capabilities.StagingProviders);
    }

    [Fact]
    public async Task Capabilities_ForAnUnknownConnection_Is404()
    {
        var response = await _client.GetAsync($"/api/connections/does-not-exist-{Guid.NewGuid():N}/capabilities");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTrips()
    {
        var name = $"conn-{Guid.NewGuid():N}";

        var putResponse = await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync($"/api/connections/{name}");
        getResponse.EnsureSuccessStatusCode();
        var connection = await getResponse.Content.ReadFromJsonAsync<ConnectionConfig>(JsonOptions);

        Assert.NotNull(connection);
        Assert.Equal(name, connection!.Name);
        Assert.Equal("sql01", connection.Host);
        Assert.NotNull(connection.CredentialSecretRef);
    }

    [Fact]
    public async Task Upsert_NeverReturnsOrCommitsPlaintextPassword()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        const string password = "sup3r-s3cr3t";

        var putResponse = await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);
        var body = await putResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, body);

        using var repo = new Repository(_factory.RepoRoot);
        var blob = (Blob)repo.Head.Tip[$"config/connections/{name}.yaml"].Target;
        Assert.DoesNotContain(password, blob.GetContentText());
    }

    [Fact]
    public async Task List_IncludesUpsertedConnection()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);

        var connections = await _client.GetFromJsonAsync<List<ConnectionConfig>>("/api/connections", JsonOptions);

        Assert.Contains(connections!, c => c.Name == name);
    }

    [Fact]
    public async Task Get_WhenNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/connections/does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var name = $"conn-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/connections/{name}", MakeInput(name), JsonOptions);

        var deleteResponse = await _client.DeleteAsync($"/api/connections/{name}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/connections/{name}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
