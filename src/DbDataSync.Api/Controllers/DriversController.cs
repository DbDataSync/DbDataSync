using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>Every registered driver — the three built-ins plus whatever <c>driver.yaml</c> descriptors
/// (phase 109d) or compiled plugins (109e) an operator has added. What the connection editor's engine
/// picker reads from instead of a hard-coded list (phase 109d), and — from phase 118 on — what the
/// admin Drivers screen renders.
/// <para>
/// <c>[Authorize(Policies.Viewer)]</c>, unchanged from before this phase — the connection editor's
/// picker is reachable by a Viewer (golden-path test 31 relies on this), so this endpoint stays at
/// that bar even though the new Libraries/known-* endpoints (118) sit at <c>Admin</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/drivers")]
public sealed class DriversController(DriverRegistry driverRegistry, ApiOptions apiOptions) : ControllerBase
{
    private static readonly HashSet<string> BuiltIn = [DriverIds.MsSql, DriverIds.Postgres, DriverIds.DuckDb];

    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<DriverSummary>> List()
    {
        var scan = DriverDescriptorScanner.Scan(apiOptions.RepoRoot).ToDictionary(e => e.DriverId);

        return Ok(driverRegistry.All
            .Select(d =>
            {
                var builtIn = BuiltIn.Contains(d.DriverType);
                var found = scan.GetValueOrDefault(d.DriverType);
                var caps = driverRegistry.Describe(d.DriverType)!;
                return new DriverSummary(
                    d.DriverType, d.DisplayName, builtIn,
                    builtIn ? "builtin" : found?.Source ?? "descriptor",
                    found?.LibraryId,
                    new DriverCapabilitySummary(
                        caps.Readers.Select(r => r.Kind).ToList(),
                        caps.StagingProviders.Select(s => s.Kind).ToList(),
                        caps.Writers.Select(w => w.Kind).ToList()));
            })
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList());
    }
}

/// <param name="Source">"builtin" for the three compiled-in drivers; "descriptor" for one loaded from
/// a <c>driver.yaml</c>; "compiled" for one loaded from a <c>driver.json</c> plugin manifest (109e).
/// Decided from <see cref="DriverIds"/> membership plus which manifest file backs the driver on disk
/// (phase 118) — not the two-way builtin/descriptor guess this DTO shipped with.</param>
/// <param name="Library">The bound <c>DbDataSync.Libraries</c> id for a descriptor driver; null for a
/// built-in or a compiled plugin (which restores its own package privately, not through a shared
/// library).</param>
public sealed record DriverSummary(
    string Id, string DisplayName, bool BuiltIn, string Source, string? Library, DriverCapabilitySummary Capabilities);

/// <summary>Kind-name-only view of <see cref="DriverCapabilities"/> for a catalogue listing — the full
/// per-Kind parameter detail belongs to the connection-scoped
/// <c>GET /api/connections/{name}/capabilities</c>, not this driver-level summary.</summary>
public sealed record DriverCapabilitySummary(
    IReadOnlyList<string> Readers, IReadOnlyList<string> Staging, IReadOnlyList<string> Writers);
