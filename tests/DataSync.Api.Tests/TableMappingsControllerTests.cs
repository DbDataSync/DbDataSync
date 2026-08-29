using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using Xunit;

namespace DataSync.Api.Tests;

public sealed class TableMappingsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

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
}
