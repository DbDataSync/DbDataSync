using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Whether this process needs a restart to pick up a change — a config value (phase 81), or (phase
/// 120) a library or driver installed/removed through the web console. A tiny, separate controller
/// rather than folding this into <see cref="AdminConfigController"/>'s own fixed
/// <c>api/admin/config</c> route, since every screen that can trigger this (Configuration, Drivers,
/// Libraries) reads the same flag, not just the config screen.
/// </summary>
[ApiController]
[Route("api/admin/restart-required")]
public sealed class AdminRestartController(RestartRequiredState state) : ControllerBase
{
    [Authorize(Policies.Admin)]
    [HttpGet]
    public ActionResult<RestartRequiredStatus> Get() => Ok(new RestartRequiredStatus(state.IsSet()));
}

public sealed record RestartRequiredStatus(bool Required);
