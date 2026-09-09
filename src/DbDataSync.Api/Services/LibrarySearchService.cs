using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Configuration;

namespace DbDataSync.Api.Services;

/// <param name="Id">The NuGet package id — what a caller would pass as <c>packageId</c> to
/// <c>POST /api/libraries</c> (phase 120) or <c>config library install</c>.</param>
public sealed record LibrarySearchResult(
    string Id, string Description, string LatestVersion, IReadOnlyList<string> Versions,
    long TotalDownloads, bool Verified);

/// <param name="Status">
/// <c>"ok"</c> (search ran; <see cref="Results"/> may still be empty), <c>"disabled"</c>
/// (<see cref="ApiOptions.NuGetSearchEnabled"/> is false — no call was made), or
/// <c>"unavailable"</c> (the call was made but failed or timed out). The SPA degrades to manual
/// package-id/version entry for anything other than <c>"ok"</c>.
/// </param>
public sealed record LibrarySearchResponse(string Status, IReadOnlyList<LibrarySearchResult>? Results);

/// <summary>
/// A read-only proxy onto the public NuGet v3 search service — no <c>NuGet.Protocol</c>, no dependency
/// resolution, just reshaping a search-index response so an operator adding an engine DbDataSync wasn't
/// built with doesn't have to already know the exact package id and an available version. See
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*Acquisition* for why a runtime NuGet
/// client was rejected — this is a different thing, a read-only index query.
/// </summary>
public sealed class LibrarySearchService(IHttpClientFactory httpClientFactory, ApiOptions apiOptions)
{
    public const string HttpClientName = "NuGetSearch";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<LibrarySearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (!apiOptions.NuGetSearchEnabled)
            return new LibrarySearchResponse("disabled", null);

        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            var url = $"https://azuresearch-usnc.nuget.org/query?q={Uri.EscapeDataString(query)}&prerelease=false";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new LibrarySearchResponse("unavailable", null);

            var payload = await response.Content.ReadFromJsonAsync<NuGetSearchPayload>(JsonOptions, cancellationToken);
            var results = (payload?.Data ?? [])
                .Select(d => new LibrarySearchResult(
                    d.Id ?? "",
                    d.Description ?? "",
                    d.Version ?? "",
                    (d.Versions ?? [])
                        .Select(v => v.Version)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => v!)
                        .ToList(),
                    d.TotalDownloads,
                    d.Verified))
                .ToList();

            return new LibrarySearchResponse("ok", results);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LibrarySearchResponse("unavailable", null);
        }
    }

    private sealed record NuGetSearchPayload(List<NuGetSearchEntry>? Data);

    private sealed record NuGetSearchEntry(
        string? Id, string? Description, string? Version, List<NuGetSearchVersionEntry>? Versions,
        long TotalDownloads, bool Verified);

    private sealed record NuGetSearchVersionEntry(string? Version, [property: JsonPropertyName("downloads")] long Downloads);
}
