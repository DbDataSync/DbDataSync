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
        Sources = [new SourceTableRef { ConnectionName = "src", Database = "App", Table = "Orders" }],
        Targets = [new TableRef { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
        ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
    };

    private async Task<string> EnsureReplicationAsync()
    {
        var replicationName = $"repl-{Guid.NewGuid():N}";
        await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
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
}
