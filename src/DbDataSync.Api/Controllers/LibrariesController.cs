using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Installed libraries, and the bundled catalogs (phase 117's <c>KnownLibraries</c>/<c>KnownDrivers</c>)
/// an "add" affordance offers. Read-only in this phase — install/remove is phase 120.
/// <para>
/// <c>[Authorize(Policies.Admin)]</c> stated explicitly on every action, per the
/// <see cref="AdminConfigController"/> precedent: this screen reveals what's installed on the host,
/// which is an admin-only bar, like the config screen.
/// </para>
/// </summary>
[ApiController]
[Route("api")]
public sealed class LibrariesController(LibrariesService service, LibrarySearchService searchService) : ControllerBase
{
    [Authorize(Policies.Admin)]
    [HttpGet("libraries")]
    public ActionResult<IReadOnlyList<LibrarySummary>> List() => Ok(service.List());

    /// <summary>
    /// A read-only proxy onto the public NuGet search index (phase 119) — see
    /// <see cref="LibrarySearchService"/> for the "disabled"/"unavailable" distinction. "disabled" is
    /// the only case reported as an actual HTTP error status; an upstream failure is still a 200 with
    /// <c>status: "unavailable"</c>, so the SPA can degrade to manual entry without treating a flaky
    /// public index as this API's own fault.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpGet("libraries/search")]
    public async Task<ActionResult<LibrarySearchResponse>> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var response = await searchService.SearchAsync(q ?? "", cancellationToken);
        return response.Status == "disabled"
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, response)
            : Ok(response);
    }

    [Authorize(Policies.Admin)]
    [HttpGet("known-libraries")]
    public ActionResult<IReadOnlyList<KnownLibrarySummary>> ListKnownLibraries() =>
        Ok(KnownLibraries.All
            .Select(e => new KnownLibrarySummary(e.Id, e.DisplayName, e.Description, e.PackageId))
            .ToList());

    [Authorize(Policies.Admin)]
    [HttpGet("known-drivers")]
    public ActionResult<IReadOnlyList<KnownDriverSummary>> ListKnownDrivers() =>
        Ok(KnownDrivers.All
            .Select(e => new KnownDriverSummary(e.Id, e.DisplayName, e.Description, e.BoundLibraryId))
            .ToList());
}

public sealed record KnownLibrarySummary(string Id, string DisplayName, string Description, string PackageId);

public sealed record KnownDriverSummary(string Id, string DisplayName, string Description, string BoundLibrary);
