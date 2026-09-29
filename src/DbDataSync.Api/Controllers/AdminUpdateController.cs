using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The console's Updates screen (phase 159; read-only since phase 196L). Admin-only, stated on every action per this
/// repository's convention. Nothing here changes the install: the status carries the CLI commands that do. Looking up
/// releases calls out to nuget.org and GitHub, so it is refused unless <c>DbDataSync:Updates:Mode</c> is <c>manual</c>.
/// </summary>
[ApiController]
[Route("api/admin/update")]
public sealed class AdminUpdateController(UpdateService updates) : ControllerBase
{
    [Authorize(Policies.Admin)]
    [HttpGet("status")]
    public ActionResult<UpdateStatusResponse> Status() => Ok(updates.Status());

    /// <summary>The releases on the enabled channels (or one), newest first — read from the pinned sources on
    /// each call, since nothing here phones home in the background. <c>502</c> when none can be reached.</summary>
    [Authorize(Policies.Admin)]
    [HttpGet("releases")]
    public async Task<ActionResult> Releases([FromQuery] string? channel, [FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        if (!updates.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = updates.DisabledReason });

        ReleaseChannel? requested = null;
        if (!string.IsNullOrEmpty(channel))
        {
            if (!channel.All(char.IsAsciiLetter) || !Enum.TryParse<ReleaseChannel>(channel, ignoreCase: true, out var parsed))
                return BadRequest(new { error = $"Unknown channel '{channel}'. Use stable, beta or snapshot." });

            if (!updates.IsChannelEnabled(parsed))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = $"The {parsed.ToString().ToLowerInvariant()} channel is not enabled (DbDataSync:Updates:Channels)." });

            requested = parsed;
        }

        var warnings = new List<string>();
        try
        {
            var releases = await updates.ListReleasesAsync(requested, Math.Clamp(limit, 1, 50), warnings, cancellationToken);
            return Ok(new { releases, warnings });
        }
        catch (ReleaseSourceException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
