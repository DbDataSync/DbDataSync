using DataSync.Api.Auth;
using DataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// The admin config screen (phase 81) — every documented <c>DataSync:*</c> key, its effective value and
/// source, and (for the ones this build can write) a way to change it. See
/// <see cref="AdminConfigService"/> for what "effective" and "source" mean and why some documented keys
/// are read-only regardless of source.
/// <para>
/// <c>[Authorize(Policies.Admin)]</c> stated explicitly on every action even though it is also the
/// fallback policy for anything unmarked — this screen can reveal which settings exist and where they
/// come from even where it withholds a value, which is a stricter bar than the <c>Viewer</c> policy most
/// read endpoints use, so it is worth saying out loud rather than relying on the fallback silently
/// agreeing.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/config")]
public sealed class AdminConfigController(AdminConfigService service, CurrentUser currentUser) : ControllerBase
{
    [Authorize(Policies.Admin)]
    [HttpGet]
    public ActionResult<IReadOnlyList<AdminConfigEntry>> List() => Ok(service.List());

    /// <summary>
    /// Writes one key into datasync.config.yaml. The same call for a direct edit of a file-sourced key
    /// and an "adopt" of a non-file-sourced one — the SPA just supplies the current effective value in
    /// the adopt case. Does not take effect in this running process until it restarts.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPut("{key}")]
    public ActionResult<AdminConfigEntry> Set(string key, [FromBody] AdminConfigSetRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Value))
            return BadRequest(new { error = "Value must not be empty." });

        try
        {
            var entry = service.Set(key, body.Value, currentUser.Author);
            return entry is null
                ? NotFound(new { error = $"'{key}' is not a DataSync:* key this screen can write." })
                : Ok(entry);
        }
        catch (DataSync.Core.Config.ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sets StateConnectionString's password, through <c>SecretStore</c> the same way
    /// <c>datasync secret set</c> does. The only key this route accepts — see CONFIG.md's "Secrets"
    /// section.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPut("{key}/secret")]
    public IActionResult SetSecret(string key, [FromBody] AdminConfigSetRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Value))
            return BadRequest(new { error = "Value must not be empty." });

        return service.SetStateConnectionSecret(key, body.Value)
            ? NoContent()
            : NotFound(new { error = $"'{key}' has no secret to set." });
    }
}

public sealed record AdminConfigSetRequest(string Value);
