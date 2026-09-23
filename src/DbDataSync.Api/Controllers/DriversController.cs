using System.Reflection;
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

    /// <summary>The driver-authoring form's own "load for editing" — the raw file, not a structured
    /// re-derivation of it, so a hand-authored field this UI's structured controls don't model round-trips
    /// unchanged (see <c>driver-yaml-authoring-ui.md</c>'s own "Edit" section).</summary>
    [Authorize(Policies.Admin)]
    [HttpGet("{id}/yaml")]
    public ActionResult<DriverYamlResponse> GetYaml(string id)
    {
        var yamlPath = Path.Combine(apiOptions.RepoRoot, "drivers", id, DriverLoader.DescriptorFileName);
        return System.IO.File.Exists(yamlPath)
            ? Ok(new DriverYamlResponse(System.IO.File.ReadAllText(yamlPath)))
            : NotFound(new { error = $"No driver.yaml exists for '{id}'." });
    }

    /// <summary>
    /// Phase 182N — lets <c>ConnectionEditPage</c> tell an operator *why* a connection's driver isn't
    /// registered, instead of the parameter form just silently rendering empty. Only called for a
    /// driver id that isn't already in <see cref="DriverRegistry.All"/> — cheap (one directory, one file
    /// read) and reruns the identical <see cref="TryBuild"/> <see cref="Create"/>/<see cref="UpdateYaml"/>/
    /// <see cref="Validate"/> already share, so the message an operator sees here matches what they'd see
    /// fixing it through the driver editor, not a third, differently-worded version of the same fact.
    /// <c>Viewer</c>, matching every other endpoint <c>ConnectionEditPage</c> already calls.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{id}/status")]
    public ActionResult<DriverStatusResult> Status(string id)
    {
        if (driverRegistry.TryGet(id, out _))
            return Ok(new DriverStatusResult(true, null));

        var yamlPath = Path.Combine(apiOptions.RepoRoot, "drivers", id, DriverLoader.DescriptorFileName);
        if (!System.IO.File.Exists(yamlPath))
            return Ok(new DriverStatusResult(false, $"No driver.yaml exists for '{id}'."));

        var (_, _, error) = TryBuild(System.IO.File.ReadAllText(yamlPath));
        return Ok(new DriverStatusResult(false, error ?? $"'{id}' failed to register for an unrecorded reason — check the server log."));
    }

    /// <summary>
    /// Phase 181N — the driver-authoring form's own "Validate" tool: the identical in-memory
    /// parse-then-build <see cref="TryBuild"/> does for <see cref="Create"/>/<see cref="UpdateYaml"/>,
    /// but standalone and never writing to disk, so it works for a brand-new unsaved id too. Returns an
    /// echo of what the descriptor actually resolved to (<see cref="Preview"/>) alongside any error —
    /// <see cref="TryBuild"/> already returns the parsed descriptor even when the later *build* step
    /// fails (a missing library, a bad <c>base</c> type, phase 178N's own <c>{password}</c> rejection),
    /// so "here's what I understood before I hit a problem" survives a build failure, not just a parse
    /// one. Built from <see cref="DriverDescriptorYaml"/> directly rather than reflecting the live
    /// <see cref="IDriver"/>/spec — reflecting a JDBC spec from this project would need a compile-time
    /// reference to <c>DbDataSync.Drivers.Jdbc</c>'s <c>java.sql</c>-aware types, exactly what this
    /// session's own IKVM architecture correction moved out of <c>DbDataSync.Api</c> for good.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpPost("validate")]
    public ActionResult<DriverValidationResult> Validate([FromBody] DriverYamlRequest body)
    {
        var (_, descriptor, error) = TryBuild(body.Yaml);
        return Ok(new DriverValidationResult(error is null, error, descriptor is null ? null : Preview(descriptor)));
    }

    /// <summary>See <see cref="Validate"/>'s own doc comment for why this reads the descriptor, not the
    /// built driver. <c>Catalog</c> mirrors <see cref="DescriptorCatalogResolution.Resolve"/>'s own
    /// choice without calling it — calling it would need a real <see cref="IDescriptorCatalog"/> default
    /// per kind (JDBC's own lives in the project this method must not reference), and the only thing
    /// worth showing here is the label, not a working instance.</summary>
    private static DriverInterpretationPreview Preview(DriverDescriptorYaml descriptor)
    {
        var isJdbc = descriptor.Base?.Contains("JdbcGenericDriver", StringComparison.Ordinal) == true;
        var keys = descriptor.Dialect.ConnectionStringKeys;

        var catalog = descriptor.Dialect.Catalog switch
        {
            null or "default" => isJdbc ? "java.sql.DatabaseMetaData" : "information_schema",
            "query" => "query",
            var other => other,
        };

        var jdbcKeys = descriptor.Jdbc?.ConnectionStringKeys;
        return new DriverInterpretationPreview(
            descriptor.Id, descriptor.DisplayName, isJdbc ? "jdbc" : "adonet",
            new DialectPreview(
                descriptor.Dialect.QuoteIdentifier, descriptor.Dialect.ParameterPrefix, descriptor.Dialect.RowLimit,
                catalog, descriptor.Dialect.DefaultDatabase, descriptor.Dialect.DefaultPort,
                new ConnectionStringKeysPreview(
                    keys?.Host ?? "Host", keys?.Port ?? "Port", keys?.Database ?? "Database",
                    keys?.Username ?? "User Id", keys?.Password ?? "Password",
                    keys?.ConnectTimeout ?? "Connect Timeout", keys?.IntegratedSecurity)),
            descriptor.TypeMap.ToDictionary(e => e.Key, e => FormatTypeMapEntry(e.Value)),
            descriptor.Capabilities.Readers, descriptor.Capabilities.Staging, descriptor.Capabilities.Writers,
            descriptor.Jdbc is null
                ? null
                : new JdbcPreview(
                    descriptor.Jdbc.DriverClass, descriptor.Jdbc.DriverJarPaths, descriptor.Jdbc.UrlTemplate,
                    new ConnectionStringKeysPreview(
                        jdbcKeys?.Host ?? "host", jdbcKeys?.Port ?? "port", jdbcKeys?.Database ?? "database",
                        jdbcKeys?.Username ?? "user", jdbcKeys?.Password ?? "password",
                        jdbcKeys?.ConnectTimeout, jdbcKeys?.IntegratedSecurity)));
    }

    private static string FormatTypeMapEntry(TypeMapEntryYaml entry)
    {
        var parts = new List<string>();
        if (entry.Precision is not null) parts.Add($"precision={entry.Precision}");
        if (entry.Scale is not null) parts.Add($"scale={entry.Scale}");
        if (entry.Length is not null) parts.Add($"length={entry.Length}");
        if (entry.Max) parts.Add("max=true");
        if (entry.Unicode) parts.Add("unicode=true");
        return parts.Count == 0 ? entry.Kind : $"{entry.Kind}({string.Join(", ", parts)})";
    }

    /// <summary>
    /// The driver-authoring form's own "create": validates entirely in memory before ever touching
    /// disk — parse, then the identical <see cref="DriverDescriptorReader.BuildDriver"/> round-trip
    /// <see cref="InstallFromCatalog"/> uses, but *before* writing rather than after, so a failure never
    /// leaves a half-written driver directory behind. <c>409</c> if the id already exists (a
    /// <c>driver.yaml</c> or a compiled <c>driver.json</c> either one — both live at the same
    /// <c>drivers/&lt;id&gt;/</c> path).
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<DriverYamlResponse>> Create([FromBody] DriverYamlRequest body)
    {
        var (driver, descriptor, error) = TryBuild(body.Yaml);
        if (error is not null)
            return BadRequest(new { error });

        var driverDir = Path.Combine(apiOptions.RepoRoot, "drivers", descriptor!.Id);
        if (Directory.Exists(driverDir))
            return Conflict(new { error = $"A driver named '{descriptor.Id}' already exists." });

        Directory.CreateDirectory(driverDir);
        await System.IO.File.WriteAllTextAsync(Path.Combine(driverDir, DriverLoader.DescriptorFileName), body.Yaml);

        driverRegistry.Register(driver!);
        restartRequired.Touch();
        return Ok(new DriverYamlResponse(body.Yaml));
    }

    /// <summary>The driver-authoring form's own "save" for an existing driver — same validate-before-write
    /// discipline as <see cref="Create"/>. Renaming isn't supported here: the yaml's own <c>id:</c> must
    /// still match <paramref name="id"/>, refused (400) otherwise rather than silently creating a second
    /// directory or orphaning the first.</summary>
    [Authorize(Policies.Admin)]
    [HttpPut("{id}/yaml")]
    public async Task<ActionResult<DriverYamlResponse>> UpdateYaml(string id, [FromBody] DriverYamlRequest body)
    {
        var driverDir = Path.Combine(apiOptions.RepoRoot, "drivers", id);
        if (!Directory.Exists(driverDir))
            return NotFound(new { error = $"No driver named '{id}' exists." });

        var (driver, descriptor, error) = TryBuild(body.Yaml);
        if (error is not null)
            return BadRequest(new { error });

        if (descriptor!.Id != id)
        {
            return BadRequest(new
            {
                error = $"The yaml's own id ('{descriptor.Id}') must match '{id}' — renaming isn't " +
                         "supported here; create a new driver instead.",
            });
        }

        await System.IO.File.WriteAllTextAsync(Path.Combine(driverDir, DriverLoader.DescriptorFileName), body.Yaml);

        driverRegistry.Register(driver!);
        restartRequired.Touch();
        return Ok(new DriverYamlResponse(body.Yaml));
    }

    /// <summary>
    /// Parse + build, entirely in memory — no disk access, so <see cref="Create"/>/<see cref="UpdateYaml"/>
    /// can validate before writing anything. <see cref="TargetInvocationException"/> is in the catch list
    /// deliberately, not an oversight: <see cref="DriverDescriptorReader.BuildDriver"/>'s JDBC path
    /// dispatches through <see cref="MethodInfo.Invoke"/>, which wraps whatever
    /// <c>JdbcGenericDriver.FromDescriptor</c> itself throws (its own <see cref="NotSupportedException"/>
    /// for a missing <c>jdbc:</c> block, say) — <see cref="InstallFromCatalog"/> never hit this because it
    /// calls <see cref="DriverDescriptorReader.ToSpec"/> directly, bypassing <c>BuildDriver</c>'s
    /// reflection dispatch entirely; this is the first caller to actually exercise that path with
    /// operator-supplied YAML, where a failure needs a clean message, not a reflection wrapper's own.
    /// </summary>
    private (IDriver? Driver, DriverDescriptorYaml? Descriptor, string? Error) TryBuild(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, null, "yaml is required.");

        DriverDescriptorYaml descriptor;
        try
        {
            descriptor = DriverDescriptorReader.Deserialize(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return (null, null, $"Invalid YAML: {ex.Message}");
        }

        try
        {
            var driver = DriverDescriptorReader.BuildDriver(descriptor, libraryRegistry, apiOptions.RepoRoot);
            return (driver, descriptor, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TargetInvocationException)
        {
            var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return (null, descriptor, InstallErrorFormatting.TailOf(real.Message));
        }
    }

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

        // A library's id is always its real NuGet package id — entry.BoundLibraryId is only the lookup
        // key into the KnownLibraries catalog, never what a library ends up installed under (see
        // architecture/planning/todo/follow-up-library-install-paths-disagree-on-the-resulting-library-id.md).
        var catalogLibrary = KnownLibraries.TryGetById(entry.BoundLibraryId)!;
        var libraryId = catalogLibrary.PackageId;

        if (!libraryRegistry.Installed.ContainsKey(libraryId))
        {
            LibraryInstaller.LibraryInstallResult result;
            try
            {
                result = await LibraryInstaller.InstallOrDeferAsync(
                    apiOptions.RepoRoot, libraryId,
                    [new PackageRef(catalogLibrary.PackageId, body.Version)], catalogLibrary.FactoryType);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = InstallErrorFormatting.TailOf(ex.Message) });
            }

            libraryRegistry.RegisterInstalled(libraryId);

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
                             $"'{libraryId}' was written but is pending restore — pass the pinned version, " +
                             "or run `config library sync` on a host with the SDK, then retry.",
                });
            }
        }

        Directory.CreateDirectory(driverDir);
        var yaml = KnownDrivers.Render(entry, entry.Id, entry.DisplayName, libraryId);
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
        return Ok(new FromCatalogResult(entry.Id, libraryId));
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

public sealed record DriverYamlRequest(string Yaml);

public sealed record DriverYamlResponse(string Yaml);

/// <summary>Phase 182N. <see cref="Error"/> is null exactly when <see cref="Registered"/> is true.</summary>
public sealed record DriverStatusResult(bool Registered, string? Error);

/// <summary>Phase 181N. <see cref="Interpreted"/> is non-null whenever the yaml at least parsed, even if
/// the later build step failed — see <see cref="DriversController.Validate"/>'s own doc comment for why
/// that's worth keeping rather than only echoing back a clean success.</summary>
public sealed record DriverValidationResult(bool Valid, string? Error, DriverInterpretationPreview? Interpreted);

public sealed record DriverInterpretationPreview(
    string Id, string DisplayName, string Base,
    DialectPreview Dialect, IReadOnlyDictionary<string, string> TypeMap,
    IReadOnlyList<string> Readers, IReadOnlyList<string> Staging, IReadOnlyList<string> Writers,
    JdbcPreview? Jdbc);

public sealed record DialectPreview(
    string QuoteIdentifier, string ParameterPrefix, string RowLimit, string Catalog,
    string DefaultDatabase, int? DefaultPort, ConnectionStringKeysPreview ConnectionStringKeys);

public sealed record ConnectionStringKeysPreview(
    string Host, string? Port, string Database, string Username, string Password,
    string? ConnectTimeout, string? IntegratedSecurity);

/// <param name="ConnectionStringKeys">JDBC's own resolved defaults (<c>host</c>/<c>port</c>/<c>database</c>/
/// <c>user</c>/<c>password</c>, <see cref="ConnectTimeout"/>/<see cref="IntegratedSecurity"/> unset unless
/// the yaml sets one — <c>JdbcGenericDriver.DefaultConnectionStringKeys</c> has no default key for either)
/// shown resolved when the yaml didn't override them, so "what key does this template actually place
/// {host} under" never requires reading source.</param>
public sealed record JdbcPreview(
    string DriverClass, IReadOnlyList<string> DriverJarPaths, string? UrlTemplate,
    ConnectionStringKeysPreview ConnectionStringKeys);
