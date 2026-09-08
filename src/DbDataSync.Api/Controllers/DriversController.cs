using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>Every registered driver — the three built-ins plus whatever <c>driver.yaml</c> descriptors
/// (phase 109d) or compiled plugins (109e) an operator has added. What the connection editor's engine
/// picker reads from instead of a hard-coded list.</summary>
[ApiController]
[Route("api/drivers")]
public sealed class DriversController(DriverRegistry driverRegistry) : ControllerBase
{
    private static readonly HashSet<string> BuiltIn = [DriverIds.MsSql, DriverIds.Postgres, DriverIds.DuckDb];

    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<DriverSummary>> List() =>
        Ok(driverRegistry.All
            .Select(d => new DriverSummary(
                d.DriverType, d.DisplayName, BuiltIn.Contains(d.DriverType),
                BuiltIn.Contains(d.DriverType) ? "builtin" : "descriptor"))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList());
}

/// <param name="Source">"builtin" for the three compiled-in drivers; "descriptor" for one loaded from
/// a <c>driver.yaml</c>. A compiled plugin (109e) will add a third value when that phase lands.</param>
public sealed record DriverSummary(string Id, string DisplayName, bool BuiltIn, string Source);
