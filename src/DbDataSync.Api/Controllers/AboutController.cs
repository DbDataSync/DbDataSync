using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>What the running instance is, for any signed-in user (phase 160).</summary>
/// <param name="Version">This build's informational version — what <c>dbdatasync version</c> prints. Null only
/// when the assembly carries none, which a packed build never does.</param>
public sealed record AboutResponse(string? Version);

/// <summary>
/// Readable by a Viewer, unlike <c>api/admin/update/status</c>, which reports the same version but is Admin-only:
/// the embedded docs viewer shows "docs for this version" to everyone who can read the docs. It is called
/// <c>about</c> rather than <c>version</c> because later phases add other things every role needs to know about
/// the instance (161K: whether Notes render rich Markdown).
/// </summary>
[ApiController]
[Route("api/about")]
public sealed class AboutController(UpdateHostFacts facts) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<AboutResponse> Get() => Ok(new AboutResponse(facts.RunningVersion));
}
