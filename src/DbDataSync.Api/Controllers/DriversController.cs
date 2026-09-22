using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.Generic;
using DbDataSync.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Api.Controllers;

/// <summary>Every registered driver — the three built-ins plus whatever <c>driver.yaml</c> descriptors
/// (phase 109d) or compiled plugins (109e) an operator has added. What the connection editor's engine
/// picker reads from instead of a hard-coded list (phase 109d), and — from phase 118 on — what the
/// admin Drivers screen renders.
/// <para>
/// <c>[Authorize(Policies.Viewer)]</c> on <see cref="List"/>, unchanged from before phase 118 — the
/// connection editor's picker is reachable by a Viewer (golden-path test 31 relies on this). The
/// mutating action phase 120 adds sits at <c>Admin</c>, like every Libraries endpoint.
/// </para>
/// </summary>
[ApiController]
[Route("api/drivers")]
public sealed class DriversController(
    DriverRegistry driverRegistry, LibraryRegistry libraryRegistry, ApiOptions apiOptions,
    RestartRequiredState restartRequired, ILogger<DriversController> logger) : ControllerBase
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

    /// <summary>
    /// The driver-authoring UI's own capability checkboxes read this rather than hardcoding a list in
    /// the SPA — <see cref="GenericDriverBase{TSpec}.SupportedReaderKinds"/>/<c>SupportedStagingKinds</c>/
    /// <c>SupportedWriterKinds</c>, which structurally cannot list a kind
    /// <see cref="GenericDriverBase{TSpec}"/>'s own reader/staging/writer construction would then reject
    /// (both derive from the same factory dictionaries — see that class's own doc comment). Any closed
    /// generic works here; the values don't depend on which <c>TSpec</c> instantiated them.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpGet("/api/known-driver-kinds")]
    public ActionResult<DriverKindsSummary> KnownKinds() => Ok(new DriverKindsSummary(
        GenericDriverBase<GenericDriverSpec>.SupportedReaderKinds,
        GenericDriverBase<GenericDriverSpec>.SupportedStagingKinds,
        GenericDriverBase<GenericDriverSpec>.SupportedWriterKinds));

    /// <summary>
    /// The one-click "add" from a <see cref="KnownDrivers"/> catalog entry (phase 120): installs the
    /// entry's bound library at <paramref name="body"/>'s version (reusing it if already installed,
    /// same as <c>config driver install</c>'s CLI behaviour), then writes the bundled descriptor with
    /// its id/displayName/library filled in. Refuses (409) if a driver with this id already exists,
    /// rather than re-pointing it.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPost("from-catalog")]
    public async Task<ActionResult<FromCatalogResult>> InstallFromCatalog([FromBody] InstallFromCatalogRequest body)
    {
        var entry = KnownDrivers.TryGetById(body.KnownDriverId);
        if (entry is null)
            return BadRequest(new { error = $"Unknown catalog driver id '{body.KnownDriverId}'." });
        if (string.IsNullOrWhiteSpace(body.Version))
            return BadRequest(new { error = "version is required." });

        var driverDir = Path.Combine(apiOptions.RepoRoot, "drivers", entry.Id);
        if (Directory.Exists(driverDir))
            return Conflict(new { error = $"A driver named '{entry.Id}' already exists." });

        if (!libraryRegistry.Installed.ContainsKey(entry.BoundLibraryId))
        {
            var catalogLibrary = KnownLibraries.TryGetById(entry.BoundLibraryId)!;
            LibraryInstaller.LibraryInstallResult result;
            try
            {
                result = await LibraryInstaller.InstallOrDeferAsync(
                    apiOptions.RepoRoot, entry.BoundLibraryId,
                    [new PackageRef(catalogLibrary.PackageId, body.Version)], catalogLibrary.FactoryType);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = InstallErrorFormatting.TailOf(ex.Message) });
            }

            libraryRegistry.RegisterInstalled(entry.BoundLibraryId);

            // On the runtime-only image (phase 121), the in-image cache only ever matches the catalog
            // entry's own pinned version — a from-catalog request for any other version can't be
            // satisfied here at all. The library's manifest is still written and registered (so it
            // shows up "pending restore" on the Libraries screen, the same as a direct
            // POST /api/libraries would leave it), but writing a driver descriptor pointing at a
            // library that cannot resolve yet would just be a second thing to notice was broken.
            if (result.Outcome == LibraryInstaller.LibraryInstallOutcome.PendingRestore)
            {
                restartRequired.Touch();
                return BadRequest(new
                {
                    error = $"No SDK is available here to restore '{catalogLibrary.PackageId}' {body.Version}, and " +
                             $"it doesn't match the in-image catalog cache's pinned version ({catalogLibrary.PinnedVersion}). " +
                             $"'{entry.BoundLibraryId}' was written but is pending restore — pass the pinned version, " +
                             "or run `config library sync` on a host with the SDK, then retry.",
                });
            }
        }

        Directory.CreateDirectory(driverDir);
        var yaml = KnownDrivers.Render(entry, entry.Id, entry.DisplayName, entry.BoundLibraryId);
        var yamlPath = Path.Combine(driverDir, DriverLoader.DescriptorFileName);
        await System.IO.File.WriteAllTextAsync(yamlPath, yaml);

        // Registered into the live DriverRegistry immediately, not left for a restart to discover —
        // the same read-the-descriptor-and-register-it step DriverLoader.LoadDescriptorDrivers does at
        // startup, just for this one new entry. A failure here is logged and skipped exactly as
        // DriverLoader's own onError contract does (a bad descriptor doesn't take the request down);
        // the descriptor is still on disk and will be retried the same way on the next real restart.
        try
        {
            var descriptor = DriverDescriptorReader.Read(yamlPath);
            var factory = libraryRegistry.GetFactory(descriptor.Library);
            var spec = DriverDescriptorReader.ToSpec(descriptor, factory);
            driverRegistry.Register(new GenericDriver(spec));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
            or YamlDotNet.Core.YamlException)
        {
            logger.LogError(ex, "Failed to load the driver descriptor just written to '{Path}'", yamlPath);
        }

        restartRequired.Touch();
        return Ok(new FromCatalogResult(entry.Id, entry.BoundLibraryId));
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

public sealed record InstallFromCatalogRequest(string KnownDriverId, string Version);

public sealed record FromCatalogResult(string Id, string Library);

/// <summary>What a `driver.yaml`'s <c>capabilities.readers</c>/<c>.staging</c>/<c>.writers</c> may
/// actually name — see <see cref="DriversController.KnownKinds"/>.</summary>
public sealed record DriverKindsSummary(
    IReadOnlyList<string> Readers, IReadOnlyList<string> Staging, IReadOnlyList<string> Writers);
