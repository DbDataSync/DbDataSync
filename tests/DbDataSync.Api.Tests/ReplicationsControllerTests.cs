using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

public sealed class ReplicationsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>A real connection row, never opened — see the identical helper and its doc comment in
    /// `TableMappingsControllerTests`, which this mirrors for the one test here that also drives
    /// `SetReadState`'s watermark-key resolution.</summary>
    private void EnsureSourceConnection(string name = "src") =>
        factory.Services.GetRequiredService<ConfigRepository>().SaveConnection(new ConnectionInput
        {
            Name = name,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, Author);

    private static ReplicationTaskConfig MakeTask(string name) => new()
    {
        Name = name,
        Enabled = true,
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
    };

    [Fact]
    public async Task Upsert_ThenGet_RoundTrips()
    {
        var name = $"repl-{Guid.NewGuid():N}";

        var putResponse = await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);
        putResponse.EnsureSuccessStatusCode();

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{name}", JsonOptions);

        Assert.NotNull(task);
        Assert.Equal(name, task!.Name);
        Assert.Equal(ScheduleMode.Continuous, task.Scheduling.Mode);
        Assert.Equal("MsSqlChangeTracking", task.ChangeProcessing.Reader.Kind);
    }

    [Fact]
    public async Task List_IncludesUpsertedReplication()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        var names = await _client.GetFromJsonAsync<List<string>>("/api/replications", JsonOptions);

        Assert.Contains(name, names!);
    }

    [Fact]
    public async Task Get_WhenNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/api/replications/does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        var deleteResponse = await _client.DeleteAsync($"/api/replications/{name}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/replications/{name}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    #region Pause history (phase 131)

    private sealed record PauseEventDto(
        long Id, string TaskName, string? MappingName, string Action, string? Note, string PerformedAtUtc, string PerformedBy);

    /// <summary>Both grains — the replication-level pause and a table-mapping's own hold — come back
    /// from the one endpoint, over the one widened `PauseEvents` table.</summary>
    [Fact]
    public async Task PauseHistory_ReturnsBothGrains()
    {
        EnsureSourceConnection();
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        await _client.PutAsJsonAsync($"/api/replications/{name}/paused",
            new { paused = true, note = "replication-wide hold" }, JsonOptions);

        var mapping = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
        };
        (await _client.PutAsJsonAsync($"/api/replications/{name}/table-mappings/orders", mapping, JsonOptions))
            .EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/replications/{name}/table-mappings/orders/read-state",
            new { intent = "Changes", hold = "Paused", note = "one table" }, JsonOptions))
            .EnsureSuccessStatusCode();

        var history = await _client.GetFromJsonAsync<List<PauseEventDto>>(
            $"/api/replications/{name}/pause-history", JsonOptions);

        Assert.Equal(2, history!.Count);
        Assert.Contains(history, e => e.MappingName == null && e.Note == "replication-wide hold");
        Assert.Contains(history, e => e.MappingName == "orders" && e.Note == "one table");
    }

    /// <summary>`limit` is honoured, the same as every other list endpoint here.</summary>
    [Fact]
    public async Task PauseHistory_HonoursLimit()
    {
        var name = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{name}", MakeTask(name), JsonOptions);

        for (var i = 0; i < 5; i++)
        {
            await _client.PutAsJsonAsync($"/api/replications/{name}/paused",
                new { paused = i % 2 == 0, note = $"note {i}" }, JsonOptions);
        }

        var history = await _client.GetFromJsonAsync<List<PauseEventDto>>(
            $"/api/replications/{name}/pause-history?limit=2", JsonOptions);

        Assert.Equal(2, history!.Count);
        Assert.Equal("note 4", history[0].Note);
        Assert.Equal("note 3", history[1].Note);
    }

    /// <summary>An unknown replication name behaves like `History` immediately above it: an empty list,
    /// not a 404 — this endpoint is not gated on `ListReplications` any more than that one is.</summary>
    [Fact]
    public async Task PauseHistory_ForUnknownReplication_IsAnEmptyList()
    {
        var response = await _client.GetAsync(
            $"/api/replications/no-such-replication-{Guid.NewGuid():N}/pause-history");

        response.EnsureSuccessStatusCode();
        var history = await response.Content.ReadFromJsonAsync<List<PauseEventDto>>(JsonOptions);
        Assert.Empty(history!);
    }

    #endregion
}
