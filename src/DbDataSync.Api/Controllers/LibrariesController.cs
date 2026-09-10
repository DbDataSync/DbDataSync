using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Installed libraries, the bundled catalogs (phase 117's <c>KnownLibraries</c>/<c>KnownDrivers</c>) an
/// "add" affordance offers, NuGet search (119), and (120) installing or removing one.
/// <para>
/// <c>[Authorize(Policies.Admin)]</c> stated explicitly on every action, per the
/// <see cref="AdminConfigController"/> precedent: this screen reveals — and, from phase 120, changes —
/// what's installed on the host, an admin-only bar, like the config screen.
/// </para>
/// </summary>
[ApiController]
[Route("api")]
public sealed class LibrariesController(
    LibrariesService service, LibrarySearchService searchService, LibraryRegistry libraryRegistry,
    ApiOptions apiOptions, RestartRequiredState restartRequired) : ControllerBase
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

    /// <summary>
    /// Installs a library — synchronously, in-process. The container's default (SDK-based, phase 120)
    /// image restores it for real via <c>dotnet publish</c>. Phase 121's runtime-only image has no SDK:
    /// there, <see cref="LibraryInstaller.InstallOrDeferAsync"/> instead copies a catalog id at its
    /// pinned version from the in-image cache, or — for anything else — writes the manifest only and
    /// leaves the library <c>PendingRestore</c> (see <see cref="LibrariesService.List"/>) until
    /// <c>config library sync</c> runs somewhere with an SDK. Either way this call still returns as
    /// soon as it can — there is no separate "pending" polling state to ask about.
    /// <para>
    /// <c>factoryType</c> may be null here: neither an explicit value nor a <see cref="KnownLibraries"/>
    /// guess is required up front any more — phase 122's reflection-assist gets a chance to find one
    /// in the restored closure before this gives up (only reachable on the SDK path; the no-SDK paths
    /// require <c>factoryType</c> to already be known, since there is no restore to scan).
    /// </para>
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPost("libraries")]
    public async Task<ActionResult<LibraryManifest>> Create([FromBody] CreateLibraryRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.PackageId) || string.IsNullOrWhiteSpace(body.Version))
            return BadRequest(new { error = "packageId and version are required." });

        var factoryType = body.FactoryType ?? KnownLibraries.TryGet(body.PackageId);

        try
        {
            var result = await LibraryInstaller.InstallOrDeferAsync(
                apiOptions.RepoRoot, body.PackageId, [new PackageRef(body.PackageId, body.Version)],
                factoryType, nugetSource: body.Source);
            // So GET /api/libraries reflects this immediately — the driver/state-store side of "load
            // once at startup" still needs a restart, but there's no reason the admin screen's own
            // listing has to lie about what's on disk in the meantime.
            libraryRegistry.RegisterInstalled(result.Manifest.Id);
            restartRequired.Touch();
            return Ok(result.Manifest);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = InstallErrorFormatting.TailOf(ex.Message) });
        }
    }

    /// <summary>Refused (409) when a driver on disk still names this library, unless
    /// <paramref name="force"/> — in-use is in-use regardless of whether the library currently
    /// resolves.</summary>
    [Authorize(Policies.Admin)]
    [HttpDelete("libraries/{id}")]
    public ActionResult Delete(string id, [FromQuery] bool force = false)
    {
        var libraryDir = LibraryPaths.LibraryDir(apiOptions.RepoRoot, id);
        if (!Directory.Exists(libraryDir))
            return NotFound(new { error = $"Library '{id}' is not installed." });

        var usedBy = service.UsedBy(id);
        if (usedBy.Count > 0 && !force)
        {
            return Conflict(new
            {
                error = $"'{id}' is still named by {usedBy.Count} driver(s): {string.Join(", ", usedBy)}. " +
                         "Pass force=true to remove it anyway.",
                usedBy,
            });
        }

        Directory.Delete(libraryDir, recursive: true);
        libraryRegistry.Remove(id);
        restartRequired.Touch();
        return NoContent();
    }
}

/// <param name="FactoryType">Required only when <paramref name="PackageId"/> isn't in
/// <see cref="KnownLibraries"/>.</param>
/// <param name="Source">An alternate NuGet feed, or null for the default.</param>
public sealed record CreateLibraryRequest(string PackageId, string Version, string? FactoryType, string? Source);

public sealed record KnownLibrarySummary(string Id, string DisplayName, string Description, string PackageId);

public sealed record KnownDriverSummary(string Id, string DisplayName, string Description, string BoundLibrary);
