using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Tests;

/// <summary>
/// That the Setup card's Apply endpoint is reachable at all.
/// <para>
/// It was not, from phase 25 until phase 40 tried to use it: the route was declared as
/// <c>{action}/apply</c>, and <c>action</c> is a reserved token in an MVC route template — it names
/// the controller method rather than binding a URL segment, so the route never matched and every
/// Apply returned a 404 that looked like the mapping was missing.
/// </para>
/// <para>
/// Deliberately a routing test and not a provisioning one: it needs no database, because what it is
/// checking happens before any of that. An unknown action reaches the service and comes back 400,
/// which is the proof that both the route matched *and* the segment bound.
/// </para>
/// </summary>
public sealed class ProvisioningRoutingTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Apply_ReachesTheService_AndTheActionSegmentIsWhatArrives()
    {
        var (replicationName, mappingName) = await EnsureMappingAsync();

        var response = await _client.PostAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}/provisioning/notAnAction/apply", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("notAnAction", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Apply_ForAMappingThatDoesNotExist_IsANotFoundFromTheHandler()
    {
        var response = await _client.PostAsync(
            $"/api/replications/no-such-replication/table-mappings/no-such-mapping/provisioning/createTargetTable/apply",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string Replication, string Mapping)> EnsureMappingAsync()
    {
        var replicationName = $"prov-{Guid.NewGuid():N}";
        const string mappingName = "orders";

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

        await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/{mappingName}", new TableMappingConfig
            {
                Name = mappingName,
                Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
                Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
                ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            }, JsonOptions);

        return (replicationName, mappingName);
    }
}
