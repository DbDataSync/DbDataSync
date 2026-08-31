using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;

namespace DataSync.Api.Tests;

/// <summary>
/// Previewing a segmenting strategy, saved and unsaved — phase 58's endpoint and phase 61's Test
/// button.
/// <para>
/// Every case here uses a <c>DuckDb</c> strategy, which by design opens no connection at all: these
/// run with no database anywhere, which is the same property that makes previewing one on picking it
/// from a list a safe thing for the product to do.
/// </para>
/// </summary>
public sealed class SegmentingPreviewTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Three months of half-open ranges, computed from nothing — no source, no target.</summary>
    private const string MonthsSql = """
        SELECT
            strftime(m, '%Y-%m')                    AS label,
            strftime(m, '%Y-%m-%d')                 AS range_start,
            strftime(m + INTERVAL 1 MONTH, '%Y-%m-%d') AS range_end,
            TRUE                                    AS selected
        FROM generate_series(DATE '2024-01-01', DATE '2024-03-01', INTERVAL 1 MONTH) AS t(m);
        """;

    private readonly HttpClient _client = factory.CreateClient();

    private sealed record PreviewResponse(List<CandidateDto> Candidates);
    private sealed record CandidateDto(string Label, bool Selected);

    private async Task<(string Replication, string Mapping)> CreateAsync(
        params SegmentingStrategyConfig[] strategies)
    {
        var name = $"seg-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
            SegmentingStrategies = [.. strategies],
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{name}/table-mappings/orders", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "db", Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "db", Schema = "dbo", Table = "Orders" }],
        }, JsonOptions)).EnsureSuccessStatusCode();

        return (name, "orders");
    }

    private static SegmentingStrategyConfig Months(string name = "by-month", string? sql = null) => new()
    {
        Name = name,
        Kind = SegmentingStrategyKind.DuckDb,
        Column = "OrderDate",
        Sql = sql ?? MonthsSql,
    };

    [Fact]
    public async Task ASavedStrategy_ProposesItsCandidates()
    {
        var (replication, mapping) = await CreateAsync(Months());

        var response = await _client.GetFromJsonAsync<PreviewResponse>(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/by-month/preview", JsonOptions);

        Assert.Equal(["2024-01", "2024-02", "2024-03"], response!.Candidates.Select(c => c.Label));
        Assert.All(response.Candidates, c => Assert.True(c.Selected));
    }

    /// <summary>
    /// The Test button. Requiring a save first would mean committing a strategy in order to find out
    /// it does not work, which is the situation the button exists to remove.
    /// </summary>
    [Fact]
    public async Task AnUnsavedStrategy_CanBePreviewedWithoutSavingIt()
    {
        var (replication, mapping) = await CreateAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/preview",
            Months("never-saved"), JsonOptions);
        response.EnsureSuccessStatusCode();

        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>(JsonOptions);
        Assert.Equal(3, preview!.Candidates.Count);

        // And it really was not saved — a Test button that writes is a Save button with the wrong label.
        var task = await _client.GetFromJsonAsync<ReplicationTaskConfig>(
            $"/api/replications/{replication}", JsonOptions);
        Assert.Empty(task!.SegmentingStrategies);
    }

    /// <summary>The whole claim of testing before saving: what the editor shows and what a backfill
    /// later proposes are produced by the same code.</summary>
    [Fact]
    public async Task TheUnsavedPreview_AgreesWithTheSavedOne()
    {
        var (replication, mapping) = await CreateAsync(Months());

        var saved = await _client.GetFromJsonAsync<PreviewResponse>(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/by-month/preview", JsonOptions);
        var unsaved = await (await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/preview",
            Months(), JsonOptions)).Content.ReadFromJsonAsync<PreviewResponse>(JsonOptions);

        Assert.Equal(saved!.Candidates, unsaved!.Candidates);
    }

    /// <summary>
    /// A malformed query is the configuration being wrong, and the form is where to say so — not a
    /// 500 that reads as the product being broken.
    /// </summary>
    [Fact]
    public async Task AQueryThatDoesNotCompile_ComesBackAsAMessage()
    {
        var (replication, mapping) = await CreateAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/preview",
            Months(sql: "SELECT this is not sql"), JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Wrong columns is the other half of the same mistake, and the one an operator is most
    /// likely to make: the query runs, and proposes nothing anyone can use.</summary>
    [Fact]
    public async Task AQueryReturningTheWrongColumns_SaysSo()
    {
        var (replication, mapping) = await CreateAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/mappings/{mapping}/segmenting/preview",
            Months(sql: "SELECT 1 AS something_else"), JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreviewingAgainstAMappingThatDoesNotExist_IsANotFound()
    {
        var (replication, _) = await CreateAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/mappings/no-such-mapping/segmenting/preview",
            Months(), JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
