using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

public sealed class RunsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>Deserialization target for the history endpoint's page shape since phase 104 — mirrors
    /// <c>DbDataSync.Api.Models.RunHistoryResponse</c> field for field.</summary>
    private sealed record RunHistoryResponseDto(List<TaskRunRecord> Runs, string? NextCursor);

    [Fact]
    public async Task Trigger_WhenReplicationDoesNotExist_Returns404()
    {
        var response = await _client.PostAsync($"/api/replications/does-not-exist-{Guid.NewGuid():N}/runs", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_WhenRunDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/runs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenNoActiveRun_Returns404()
    {
        var response = await _client.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logs_WhenRunDoesNotExist_ReturnsEmptyList()
    {
        var response = await _client.GetAsync($"/api/runs/{Guid.NewGuid()}/logs");
        response.EnsureSuccessStatusCode();
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    // ---- Phase 104: the history endpoint's new query parameters ---------------------------------

    /// <summary>
    /// Every one of the endpoint's new query parameters — <c>kind</c>, <c>mappingName</c>,
    /// <c>status</c> — maps through to the store's own filters, not just the pre-existing
    /// <c>kind</c>/<c>limit</c> pair. Four runs, each matching all but one of the three filters, so a
    /// query that silently dropped one of them would still show as passing this if it only exercised
    /// the filters one at a time.
    /// </summary>
    [Fact]
    public async Task History_FiltersByKindMappingAndStatus_TogetherAndAlone()
    {
        var taskName = $"runs-hist-{Guid.NewGuid():N}";
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();

        Guid Complete(RunKind kind, string mapping, RunStatus status, string segment = "")
        {
            var runId = queue.Enqueue(taskName, kind, mapping, segmentLabel: segment);
            runs.BeginRun(runId, pid: null);
            runs.CompleteRun(runId, status, 0, 0, status == RunStatus.Failed ? "boom" : null);
            return runId;
        }

        var match = Complete(RunKind.BulkLoad, "orders", RunStatus.Failed);
        Complete(RunKind.Primary, "orders", RunStatus.Failed); // wrong kind
        Complete(RunKind.BulkLoad, "customers", RunStatus.Failed); // wrong mapping
        // Same (kind, mapping) as `match` — a distinct segment keeps this from deduping onto match's
        // own work-queue row (WorkQueueStore's in-flight uniqueness is keyed on all four).
        Complete(RunKind.BulkLoad, "orders", RunStatus.Succeeded, segment: "seg-2"); // wrong status

        var combined = await GetHistoryAsync(
            taskName, kind: "BulkLoad", mappingName: "orders", status: "Failed");

        Assert.Single(combined.Runs);
        Assert.Equal(match, combined.Runs[0].RunId);
        Assert.Null(combined.NextCursor);
    }

    /// <summary>A filter matching nothing is an empty page and a null cursor — never one that would
    /// loop back to a first page that was also empty.</summary>
    [Fact]
    public async Task History_AFilterMatchingNothing_ReturnsAnEmptyPage()
    {
        var taskName = $"runs-hist-{Guid.NewGuid():N}";
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();

        var runId = queue.Enqueue(taskName, RunKind.Primary, "orders");
        runs.BeginRun(runId, pid: null);
        runs.CompleteRun(runId, RunStatus.Succeeded, 0, 0, null);

        var page = await GetHistoryAsync(taskName, mappingName: "does-not-exist");

        Assert.Empty(page.Runs);
        Assert.Null(page.NextCursor);
    }

    /// <summary>
    /// The endpoint's own cursor, round-tripped: a page's <c>nextCursor</c> fed back as <c>cursor</c>
    /// resumes exactly where that page left off, and the last page reports a null cursor rather than
    /// one that would loop.
    /// </summary>
    [Fact]
    public async Task History_Cursor_ResumesWhereThePreviousPageLeftOff()
    {
        var taskName = $"runs-hist-{Guid.NewGuid():N}";
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<TaskRunStore>();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var runId = queue.Enqueue(taskName, RunKind.Primary, $"mapping-{i}");
            runs.BeginRun(runId, pid: null);
            runs.CompleteRun(runId, RunStatus.Succeeded, 0, 0, null);
            ids.Add(runId);
            await Task.Delay(5);
        }

        var page1 = await GetHistoryAsync(taskName, limit: 2);
        Assert.Equal(2, page1.Runs.Count);
        Assert.NotNull(page1.NextCursor);

        var page2 = await GetHistoryAsync(taskName, limit: 2, cursor: page1.NextCursor);
        Assert.Single(page2.Runs);
        Assert.Equal(ids[0], page2.Runs[0].RunId); // the oldest of the three
        Assert.Null(page2.NextCursor);
    }

    private async Task<RunHistoryResponseDto> GetHistoryAsync(
        string taskName, string? kind = null, string? mappingName = null, string? status = null,
        string? cursor = null, int? limit = null)
    {
        var query = new List<string>();
        if (kind is not null) query.Add($"kind={kind}");
        if (mappingName is not null) query.Add($"mappingName={Uri.EscapeDataString(mappingName)}");
        if (status is not null) query.Add($"status={status}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (limit is not null) query.Add($"limit={limit}");
        var queryString = query.Count == 0 ? "" : "?" + string.Join("&", query);

        var response = await _client.GetAsync($"/api/replications/{taskName}/runs{queryString}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RunHistoryResponseDto>(JsonOptions);
        Assert.NotNull(body);
        return body!;
    }
}
