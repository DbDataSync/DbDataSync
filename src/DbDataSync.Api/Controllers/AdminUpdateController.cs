using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Updating this installation from the console (phase 159). Admin-only, stated on every action per this
/// repository's convention, and closed by default: <c>apply</c> is refused unless
/// <c>DbDataSync:SelfUpdateEnabled</c> is set.
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
        var capability = updates.Capability();
        if (!capability.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = capability.Reason });

        ReleaseChannel? requested = null;
        if (!string.IsNullOrEmpty(channel))
        {
            if (!channel.All(char.IsAsciiLetter) || !Enum.TryParse<ReleaseChannel>(channel, ignoreCase: true, out var parsed))
                return BadRequest(new { error = $"Unknown channel '{channel}'. Use stable, beta or snapshot." });

            if (!updates.IsChannelEnabled(parsed))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = $"The {parsed.ToString().ToLowerInvariant()} channel is not enabled (DbDataSync:SelfUpdateChannels)." });

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

    [Authorize(Policies.Admin)]
    [HttpPost("apply")]
    public async Task<ActionResult> Apply([FromBody] ApplyUpdateRequest body, CancellationToken cancellationToken)
    {
        var result = await updates.RequestAsync(body.Version ?? "", User.Identity?.Name, cancellationToken);

        return result.Outcome switch
        {
            UpdateRequestOutcome.Accepted => Accepted(new { message = result.Message }),
            UpdateRequestOutcome.Disabled or UpdateRequestOutcome.ChannelNotEnabled => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Message }),
            UpdateRequestOutcome.InvalidVersion => BadRequest(new { error = result.Message }),
            UpdateRequestOutcome.NotFound => NotFound(new { error = result.Message }),
            UpdateRequestOutcome.SourceUnavailable => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Message }),
            _ => Conflict(new { error = result.Message }),
        };
    }
}

/// <param name="Version">The version to install. Looked up in the pinned release sources; nothing else about
/// the update — where it comes from, what package — is taken from the request.</param>
public sealed record ApplyUpdateRequest(string? Version);
