using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

public sealed class TableMappingsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>A real connection row, never opened — <c>SetReadState</c>'s watermark-key resolution
    /// needs to load it (to find which dialect spelled the key), the same reasoning
    /// <c>MappingReadStateTests.SetUpAsync</c> already documents. Saved directly through
    /// <c>ConfigRepository</c> rather than the connections endpoint for the same pre-existing
    /// environment reason that file gives (Negotiate auth throws under this sandbox's TestServer).</summary>
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

    private static TableMappingConfig MakeMapping(string name) => new()
    {
        Name = name,
        Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
    };

    private async Task<string> EnsureReplicationAsync(TaskEndpoints? endpoints = null)
    {
        var replicationName = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
            Endpoints = endpoints ?? new TaskEndpoints(),
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions);
        return replicationName;
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTrips()
    {
        var replicationName = await EnsureReplicationAsync();
        const string mappingName = "orders";

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}", MakeMapping(mappingName), JsonOptions);
        putResponse.EnsureSuccessStatusCode();

        var mapping = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}", JsonOptions);

        Assert.NotNull(mapping);
        Assert.Single(mapping!.Sources);
        Assert.Equal("Orders", mapping.Sources[0].Table);
    }

    [Fact]
    public async Task List_IncludesUpsertedMapping()
    {
        var replicationName = await EnsureReplicationAsync();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        var names = await _client.GetFromJsonAsync<List<string>>(
            $"/api/replications/{replicationName}/table-mappings", JsonOptions);

        Assert.Contains("orders", names!);
    }

    [Fact]
    public async Task Delete_ThenGet_Returns404()
    {
        var replicationName = await EnsureReplicationAsync();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        var deleteResponse = await _client.DeleteAsync($"/api/replications/{replicationName}/table-mappings/orders");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/replications/{replicationName}/table-mappings/orders");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task InheritingAndOverridingMappings_CoexistInOneReplication()
    {
        var replicationName = await EnsureReplicationAsync(new TaskEndpoints
        {
            Source = new EndpointRef { ConnectionName = "prod-src", Database = "App" },
            Target = new EndpointRef { ConnectionName = "warehouse", Database = "DW" },
        });

        // States nothing but schema and table: the whole point of the replication-level endpoints.
        var inheriting = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { Table = "Orders" }],
            Targets = [new TableSpec { Table = "Orders" }],
        };
        // The archive lives on a different database of the same source connection — one field
        // overridden, the rest still inherited.
        var overriding = new TableMappingConfig
        {
            Name = "archive",
            Sources = [new SourceTableSpec { Database = "AppArchive", Table = "Orders" }],
            Targets = [new TableSpec { Table = "OrdersArchive" }],
        };

        foreach (var mapping in new[] { inheriting, overriding })
        {
            var response = await _client.PutAsJsonAsync(
                $"/api/replications/{replicationName}/table-mappings/{mapping.Name}", mapping, JsonOptions);
            response.EnsureSuccessStatusCode();
        }

        // Round-tripped config keeps the mappings sparse — inheritance is resolved at use, not baked
        // in on save, so changing the replication's endpoint moves every mapping that inherits it.
        var saved = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replicationName}/table-mappings/orders", JsonOptions);
        Assert.Null(saved!.Sources[0].ConnectionName);
        Assert.Null(saved.Sources[0].Database);

        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>($"/api/replications/{replicationName}", JsonOptions);
        Assert.Equal("prod-src", EndpointResolution.ResolveSource(task!, saved.Sources[0]).ConnectionName);
        Assert.Equal("App", EndpointResolution.ResolveSource(task!, saved.Sources[0]).Database);

        var savedOverride = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replicationName}/table-mappings/archive", JsonOptions);
        var resolved = EndpointResolution.ResolveSource(task!, savedOverride!.Sources[0]);
        Assert.Equal("prod-src", resolved.ConnectionName);
        Assert.Equal("AppArchive", resolved.Database);
        Assert.Equal("warehouse", EndpointResolution.ResolveTarget(task!, savedOverride.Targets[0]).ConnectionName);
    }

    [Fact]
    public async Task Upsert_WhenNothingSuppliesTheEndpoint_Returns400()
    {
        var replicationName = await EnsureReplicationAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders",
            new TableMappingConfig
            {
                Name = "orders",
                Sources = [new SourceTableSpec { Table = "Orders" }],
                Targets = [new TableSpec { Table = "Orders" }],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("connection", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_ForUnknownReplication_Returns404()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/replications/no-such-replication/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #region Bulk creation

    /// <summary>A bulk-created mapping states only its tables, so the replication has to be the one
    /// that says where they live — which is exactly the arrangement bulk creation assumes.</summary>
    private static readonly TaskEndpoints Endpoints = new()
    {
        Source = new EndpointRef { ConnectionName = "prod-src", Database = "App" },
        Target = new EndpointRef { ConnectionName = "warehouse", Database = "DW" },
    };

    /// <summary>
    /// One call rather than N. Forty tables through the single-mapping endpoint is forty round trips,
    /// forty git commits and forty chances to stop half way with no record of where.
    /// </summary>
    [Fact]
    public async Task BulkCreate_CreatesOneMappingPerTable_NamedAfterIt()
    {
        var replicationName = await EnsureReplicationAsync(Endpoints);

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk",
            new { tables = new[] { new { schema = "dbo", table = "Orders" }, new { schema = "sales", table = "Lines" } } },
            JsonOptions);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BulkResultDto>(JsonOptions);

        Assert.Equal(["dbo.Orders", "sales.Lines"], result!.Created);
        Assert.Empty(result.Skipped);

        var mapping = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replicationName}/table-mappings/sales.Lines", JsonOptions);
        Assert.Equal("sales", mapping!.Sources[0].Schema);
        Assert.Equal("Lines", mapping.Sources[0].Table);
        Assert.Equal("sales", mapping.Targets[0].Schema);
        Assert.Equal("Lines", mapping.Targets[0].Table);
    }

    /// <summary>
    /// Everything not stated is left unset. That is the point of creating in bulk: forty mappings that
    /// follow the replication, not forty copies of its settings that stop following it the day one is
    /// changed.
    /// </summary>
    [Fact]
    public async Task BulkCreate_LeavesEverythingElseInherited()
    {
        var replicationName = await EnsureReplicationAsync(Endpoints);

        (await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk",
            new { tables = new[] { new { schema = "dbo", table = "Orders" } } }, JsonOptions))
            .EnsureSuccessStatusCode();

        var mapping = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{replicationName}/table-mappings/dbo.Orders", JsonOptions);

        Assert.Null(mapping!.Sources[0].ConnectionName);
        Assert.Null(mapping.Sources[0].Database);
        Assert.Null(mapping.Targets[0].ConnectionName);
        Assert.Null(mapping.Targets[0].Database);
        Assert.Null(mapping.Provisioning.CreateTargetTableIfMissing);
        Assert.Empty(mapping.Scripts);
    }

    /// <summary>
    /// Ticking every row on a replication that already maps half of them means "map the rest", so an
    /// existing mapping is a skip and not a failure — and it is reported, so "create 40" answering
    /// with 12 is explained rather than looking broken.
    /// </summary>
    [Fact]
    public async Task BulkCreate_SkipsTablesThatAlreadyHaveAMapping_RatherThanFailing()
    {
        var replicationName = await EnsureReplicationAsync(Endpoints);
        var tables = new { tables = new[] { new { schema = "dbo", table = "Orders" } } };

        (await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk", tables, JsonOptions))
            .EnsureSuccessStatusCode();

        var second = await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk", tables, JsonOptions);

        second.EnsureSuccessStatusCode();
        var result = await second.Content.ReadFromJsonAsync<BulkResultDto>(JsonOptions);
        Assert.Empty(result!.Created);
        Assert.Equal(["dbo.Orders"], result.Skipped);
    }

    /// <summary>A batch that names the same table twice creates it once — the second occurrence is
    /// the mapping the first one just made, not a second mapping of the same name overwriting it.</summary>
    [Fact]
    public async Task BulkCreate_DoesNotCreateTheSameTableTwiceWithinOneBatch()
    {
        var replicationName = await EnsureReplicationAsync(Endpoints);

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk",
            new { tables = new[] { new { schema = "dbo", table = "Orders" }, new { schema = "dbo", table = "Orders" } } },
            JsonOptions);

        var result = await response.Content.ReadFromJsonAsync<BulkResultDto>(JsonOptions);
        Assert.Equal(["dbo.Orders"], result!.Created);
        Assert.Equal(["dbo.Orders"], result.Skipped);
    }

    [Fact]
    public async Task BulkCreate_WithNoTables_IsABadRequest()
    {
        var replicationName = await EnsureReplicationAsync(Endpoints);

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/bulk",
            new { tables = Array.Empty<object>() }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkCreate_AgainstAMissingReplication_IsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/replications/no-such-replication/table-mappings/bulk",
            new { tables = new[] { new { schema = "dbo", table = "Orders" } } }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record BulkResultDto(List<string> Created, List<string> Skipped);

    #endregion

    #region Cached column metadata (phase 90)

    /// <summary>
    /// That the capture rule <c>MappingMetadataTests</c> pins is the one actually on the route — the
    /// same reason phase 86's lag tests went through the endpoint as well as the function. A rule
    /// nothing calls is a rule that holds perfectly and protects nothing.
    /// </summary>
    [Fact]
    public async Task Upsert_CapturesTheColumnsTheEditorSends_ThenLeavesThemAloneOnAnUnrelatedSave()
    {
        var replicationName = await EnsureReplicationAsync();
        var url = $"/api/replications/{replicationName}/table-mappings/orders";

        var created = MakeMapping("orders");
        created.SourceColumns = [new CachedColumn("Id", "int", false, true, true)];
        created.TargetColumns = [new CachedColumn("Id", "bigint", false, true, false)];
        (await _client.PutAsJsonAsync(url, created, JsonOptions)).EnsureSuccessStatusCode();

        var afterCreate = await _client.GetFromJsonAsync<TableMappingConfig>(url, JsonOptions);
        Assert.Equal("int", Assert.Single(afterCreate!.SourceColumns).NativeType);
        var capturedAt = afterCreate.ColumnsCapturedUtc;
        Assert.NotNull(capturedAt);

        // An edit to something the cache has nothing to do with, sending the cache back unchanged —
        // which is exactly what the editor does when neither side's table was touched.
        var edited = MakeMapping("orders");
        edited.Notes = "the warehouse team owns this one";
        edited.SourceColumns = afterCreate.SourceColumns;
        edited.TargetColumns = afterCreate.TargetColumns;
        (await _client.PutAsJsonAsync(url, edited, JsonOptions)).EnsureSuccessStatusCode();

        var afterEdit = await _client.GetFromJsonAsync<TableMappingConfig>(url, JsonOptions);
        Assert.Equal(capturedAt, afterEdit!.ColumnsCapturedUtc);
        Assert.Equal("bigint", Assert.Single(afterEdit.TargetColumns).NativeType);
    }

    [Fact]
    public async Task Upsert_FromAClientThatKnowsNothingOfTheCache_DoesNotClearIt()
    {
        var replicationName = await EnsureReplicationAsync();
        var url = $"/api/replications/{replicationName}/table-mappings/orders";

        var created = MakeMapping("orders");
        created.SourceColumns = [new CachedColumn("Id", "int", false, true, true)];
        (await _client.PutAsJsonAsync(url, created, JsonOptions)).EnsureSuccessStatusCode();

        // MakeMapping states no columns at all — the shape every write path that predates this phase
        // sends, the CLI and a hand-rolled curl among them.
        (await _client.PutAsJsonAsync(url, MakeMapping("orders"), JsonOptions)).EnsureSuccessStatusCode();

        var saved = await _client.GetFromJsonAsync<TableMappingConfig>(url, JsonOptions);
        Assert.Equal("Id", Assert.Single(saved!.SourceColumns).Name);
    }

    [Fact]
    public async Task RefreshMetadata_OnAMappingThatIsNotThere_IsNotFound()
    {
        var replicationName = await EnsureReplicationAsync();

        var response = await _client.PostAsync(
            $"/api/replications/{replicationName}/table-mappings/no-such-mapping/refresh-metadata", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Read-state pause history (phase 131)

    private async Task<string> SetReadStateAsync(
        string replicationName, string mappingName, object body)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}/read-state", body, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<List<PauseEventDto>> GetPauseHistoryAsync(string replicationName) =>
        (await _client.GetFromJsonAsync<List<PauseEventDto>>(
            $"/api/replications/{replicationName}/pause-history", JsonOptions))!;

    /// <summary>Setting `Hold: Paused` on a mapping with no prior hold writes exactly one `PauseEvents`
    /// row for that mapping — the boundary crossing `TableMappingsController.SetReadState` is supposed
    /// to catch.</summary>
    [Fact]
    public async Task SetReadState_Pausing_WritesExactlyOnePauseEventRow()
    {
        var replicationName = await EnsureReplicationAsync();
        EnsureSourceConnection();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        await SetReadStateAsync(replicationName, "orders",
            new { intent = "Changes", hold = "Paused", note = "waiting on the DBA" });

        var history = await GetPauseHistoryAsync(replicationName);

        var row = Assert.Single(history);
        Assert.Equal("orders", row.MappingName);
        Assert.Equal("Paused", row.Action);
        Assert.Equal("waiting on the DBA", row.Note);
    }

    /// <summary>Toggling a paused mapping back to `None` writes a `Resumed` row — the other half of the
    /// boundary.</summary>
    [Fact]
    public async Task SetReadState_Resuming_WritesAResumedRow()
    {
        var replicationName = await EnsureReplicationAsync();
        EnsureSourceConnection();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        await SetReadStateAsync(replicationName, "orders", new { intent = "Changes", hold = "Paused" });
        await SetReadStateAsync(replicationName, "orders", new { intent = "Changes", hold = "None" });

        var history = await GetPauseHistoryAsync(replicationName);

        Assert.Equal(2, history.Count);
        Assert.Equal("Resumed", history[0].Action);
        Assert.Equal("Paused", history[1].Action);
    }

    /// <summary>Changing only `Intent`, with `Hold` unchanged (and never `Paused`), writes nothing —
    /// this is not an audit trail of every call the endpoint receives, only of pause boundaries.</summary>
    [Fact]
    public async Task SetReadState_IntentOnlyChange_WritesNoPauseEvent()
    {
        var replicationName = await EnsureReplicationAsync();
        EnsureSourceConnection();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        await SetReadStateAsync(replicationName, "orders", new { intent = "InitialLoad", hold = "None" });
        await SetReadStateAsync(replicationName, "orders", new { intent = "Changes", hold = "None" });

        Assert.Empty(await GetPauseHistoryAsync(replicationName));
    }

    /// <summary>A `PositionExpired` -&gt; `None` recovery that never crossed `Paused` writes nothing
    /// either — the same "only a real pause boundary" rule, from the other hold value that isn't
    /// `Paused`.</summary>
    [Fact]
    public async Task SetReadState_PositionExpiredRecovery_ThatNeverTouchedPaused_WritesNoPauseEvent()
    {
        var replicationName = await EnsureReplicationAsync();
        EnsureSourceConnection();
        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", MakeMapping("orders"), JsonOptions);

        // The endpoint itself does not validate Hold against what actually produced it (phase 101's
        // reader is what raises PositionExpired for real, on a failed pass), so this drives the same
        // transition through the one call surface this controller exposes: into PositionExpired first
        // (never Paused, so no history), then the recovery back to None (still never Paused).
        await SetReadStateAsync(replicationName, "orders", new { intent = "Changes", hold = "PositionExpired" });
        await SetReadStateAsync(replicationName, "orders", new { intent = "ChangesFromEarliest", hold = "None" });

        Assert.Empty(await GetPauseHistoryAsync(replicationName));
    }

    private sealed record PauseEventDto(
        long Id, string TaskName, string? MappingName, string Action, string? Note, string PerformedAtUtc, string PerformedBy);

    #endregion
}
