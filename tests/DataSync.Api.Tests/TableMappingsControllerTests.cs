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
}
