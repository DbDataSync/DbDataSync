using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Controllers;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 134: a mapping whose reader captures its own position (Change Tracking, CDC, TriggerAudit)
/// never loads on its own first Primary pass — that pass only captures a position and requests a Bulk
/// Load, which the real worker process these Integration-tagged tests spawn (see
/// <see cref="TestApiFactory"/>'s own doc) drains concurrently. Waiting for the *Primary* run's own row
/// to go terminal — what every test using this pattern already did before phase 134 — is not the same
/// as waiting for that Bulk Load to actually finish; a test that reads real target rows, or triggers a
/// further pass, immediately afterward races it.
/// <para>
/// This is the same thing <c>SchedulerService.FilterHeld</c> already waits for before ever handing a
/// real mapping a further Primary pass (<see cref="ReadHold.Loading"/> — phase 134's own retrospective
/// is explicit that this is the only enforcement point, by design): poll the mapping's own read-state
/// endpoint until its hold clears.
/// </para>
/// </summary>
internal static class MappingLoadWaiter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static async Task WaitForLoadToCompleteAsync(
        this HttpClient client, string replicationName, string mappingName, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync(
                $"/api/replications/{replicationName}/table-mappings/{mappingName}/read-state");
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
            if (state!.Hold != ReadHold.Loading)
                return;
            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"'{mappingName}' on '{replicationName}' was still Loading after " +
            $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s.");
    }
}
