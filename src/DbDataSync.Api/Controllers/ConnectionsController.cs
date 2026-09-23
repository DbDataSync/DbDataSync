using DbDataSync.Core.Config;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Api.Auth;
using DbDataSync.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    DriverConnectionFactory connectionFactory,
    ParameterCheck parameterCheck,
    ScriptTestService testService,
    CurrentUser currentUser,
    SecretStore secrets,
    LibraryRegistry libraryRegistry,
    ApiOptions apiOptions,
    LibraryValidationLauncher libraryValidationLauncher) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<ConnectionConfig>> List() =>
        Ok(configRepository.ListConnections().Select(configRepository.LoadConnection).ToList());

    [Authorize(Policies.Viewer)]
    [HttpGet("{name}")]
    public ActionResult<ConnectionConfig> Get(string name)
    {
        try
        {
            return Ok(configRepository.LoadConnection(name));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// The reader/cache/writer Kinds this connection's engine actually supports, and which of them
    /// support segmentation or reconciliation. Everything here comes from the registered driver's own
    /// declarative properties, so a UI can build its Kind pickers against whatever drivers are
    /// registered rather than against a hardcoded list that goes stale the moment a second one exists.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/capabilities")]
    public ActionResult<DriverCapabilities> Capabilities(string name)
    {
        ConnectionConfig connection;
        try
        {
            connection = configRepository.LoadConnection(name);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        var capabilities = driverRegistry.Describe(connection.DriverType);
        return capabilities is null
            ? NotFound(new { error = $"No driver is registered for '{connection.DriverType}'." })
            : Ok(capabilities);
    }

    /// <summary>
    /// The same, for a driver type rather than a saved connection.
    /// <para>
    /// A connection being created has no capabilities to look up by name, and it still has to offer
    /// the driver's settings — which was fine while the connection form hardcoded them and stopped
    /// being fine the moment a driver started declaring them (phase 42). Asking by type is the
    /// question the new-connection screen actually has.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("~/api/drivers/{driverType}/capabilities")]
    public ActionResult<DriverCapabilities> CapabilitiesForDriver(string driverType)
    {
        var capabilities = driverRegistry.Describe(driverType);
        return capabilities is null
            ? NotFound(new { error = $"No driver is registered for '{driverType}'." })
            : Ok(capabilities);
    }

    /// <summary>
    /// What a connection of this driver takes, given what it has been given so far.
    /// <para>
    /// A POST, and separate from capabilities, because the answer depends on the values: Host is not a
    /// setting once the operator picks connection-string addressing. Capabilities stays a cacheable GET
    /// that the Kind pickers read — nothing about a reader's options changes when somebody edits a
    /// host field, and making the whole response values-dependent would refetch all of it on every
    /// dropdown change.
    /// </para>
    /// <para>
    /// A body rather than a query string because this is an arbitrary bag of operator-typed values,
    /// including a properties vararg whose keys nobody here chose. Nothing sent here is stored, and a
    /// credential is deliberately not among the values the client sends: only the two settings marked
    /// <c>recalc</c> change the answer.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpPost("~/api/drivers/{driverType}/connection-parameters")]
    public ActionResult<IReadOnlyList<ParameterDescriptor>> ConnectionParameters(
        string driverType, [FromBody] Dictionary<string, string>? values)
    {
        if (!driverRegistry.TryGet(driverType, out var driver))
            return NotFound(new { error = $"No driver is registered for '{driverType}'." });

        return Ok(driver.ConnectionParameters(values ?? []));
    }

    /// <summary>
    /// Opens the connection and runs the driver's probe.
    /// <para>
    /// A failure is reported as <c>succeeded: false</c> with the provider's message, not as a 500: an
    /// unreachable database is an answer to the question this endpoint asks, and a 500 would tell the
    /// operator the console is broken rather than the database is.
    /// </para>
    /// <para>
    /// Phase 176M: the catch below widened from an allowlist (<see cref="DbException"/>/
    /// <see cref="InvalidOperationException"/>/<see cref="SocketException"/>) to everything except
    /// <see cref="OperationCanceledException"/> — a raw <c>java.sql.SQLException</c> isn't a
    /// <see cref="DbException"/> subtype and used to escape the old allowlist entirely here, becoming an
    /// unhandled 500 that directly contradicted this method's own "not as a 500" promise above. That
    /// specific escape is now fixed at its source instead (<c>DbDataSync.Drivers.Jdbc</c> translates every
    /// <c>java.sql.SQLException</c> into <c>Jdbc.Ado.JdbcSqlException</c>, a plain <see cref="DbException"/>,
    /// before it ever leaves that project) — the widened catch stays regardless, as a general safety net
    /// for any provider's non-<see cref="DbException"/> failure, JDBC included but not JDBC-only. Both the
    /// success and failure paths also fold in <see cref="IConnectionPreviewer.PreviewConnection"/>'s
    /// output (when the driver implements it) — most valuable on failure, since seeing what was actually
    /// resolved and attempted is the diagnostic value an operator needs precisely then.
    /// </para>
    /// </summary>
    [HttpPost("{name}/test")]
    public async Task<ActionResult<ConnectionTestReport>> Test(string name, CancellationToken cancellationToken)
    {
        ConnectionConfig connection;
        try
        {
            connection = configRepository.LoadConnection(name);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        if (!driverRegistry.TryGet(connection.DriverType, out var driver))
            return NotFound(new { error = $"No driver is registered for '{connection.DriverType}'." });

        if (driver is not IConnectionTester tester)
            return BadRequest(new { error = $"The '{connection.DriverType}' driver cannot test a connection." });

        // The same credential DriverConnectionFactory.OpenAsync resolves internally, resolved again here
        // — that method doesn't hand the credential back to its caller, and this is the one value
        // ConnectionDiagnostics.Redact needs to scrub it from a preview or an error message.
        var credential = connection.AuthMode == AuthMode.SqlAuth
            ? secrets.Resolve(connection.CredentialSecretRef!)
            : null;

        var started = Stopwatch.GetTimestamp();
        DbConnection? open = null;
        try
        {
            // Never touches the network — safe to compute before OpenAsync, and if the config itself is
            // bad enough that even this throws (e.g. SqlAuth with no UserId), OpenAsync is about to hit
            // the identical check and report it through the catch below anyway.
            var preview = driver is IConnectionPreviewer previewer ? previewer.PreviewConnection(connection) : null;

            (open, _) = await connectionFactory.OpenAsync(name, cancellationToken);
            var connectMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var result = await tester.TestAsync(open, cancellationToken);

            // Phase 109j item 5: the static, no-execution compatibility check's result surfaces here —
            // it's already computed (from `library install`/`config check` time), cheap, and in-process,
            // so a successful Test Connection is the natural place to mention it. Only on success: a
            // connection that can't even connect has a more pressing problem than a library warning.
            string? libraryWarning = null;
            QueryPreviewResult? testQueryResult = null;
            if (result.Succeeded)
            {
                var compatibility = DriverLibraryCompatibility.Check(driver, libraryRegistry, apiOptions.RepoRoot);
                if (compatibility is { Compatible: false })
                {
                    libraryWarning =
                        $"Connected, but the installed {compatibility.AssemblyName} is missing " +
                        $"{compatibility.MissingMembers.Count} member(s) this driver uses — " +
                        "Validate library for a full check.";
                }

                // The connection's own override, falling back to the driver's default sample query —
                // same as ConnectionsController's own capabilities response works out DefaultTestQuery.
                // Only attempted once reachability is already proven: a connection that can't even
                // connect has nothing this would add, and running it here would just report the same
                // failure a second time under a different label.
                var testQuery = connection.TestQuery ?? tester.DefaultTestQuery;
                if (!string.IsNullOrWhiteSpace(testQuery))
                    testQueryResult = await testService.PreviewTestQueryAsync(open, name, testQuery, cancellationToken);
            }

            return Ok(BuildTestReport(
                result.Succeeded, connectMs, result.RoundTrip.TotalMilliseconds, result.ServerVersion,
                result.Error, libraryWarning, preview, credential, testQueryResult));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failing to *open* is the most common failure and never reaches the driver's probe, so it
            // is reported in the same shape rather than as an error the SPA has to handle separately.
            // preview isn't reachable here even when the try's own PreviewConnection call is what threw
            // — it's a local of the try block. Not a real loss: a config broken enough to fail
            // PreviewConnection fails OpenAsync for the identical reason, right after.
            return Ok(BuildTestReport(
                succeeded: false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, probeMs: 0, serverVersion: null,
                ConnectionDiagnostics.Describe(ex), libraryWarning: null, preview: null, credential, testQueryResult: null));
        }
        finally
        {
            if (open is not null)
                await open.DisposeAsync();
        }
    }

    /// <summary>
    /// Assembles a <see cref="ConnectionTestReport"/> from a <see cref="Test"/> attempt (success or
    /// failure) and its optional <see cref="ConnectionPreview"/>, redacting <paramref name="credential"/>
    /// out of every text field it appears in — <see cref="ConnectionDiagnostics.Redact"/>'s own "applied
    /// once at the boundary" rule, so no caller of this method needs to remember to redact anything
    /// itself.
    /// </summary>
    private static ConnectionTestReport BuildTestReport(
        bool succeeded, double connectMs, double probeMs, string? serverVersion, string? error,
        string? libraryWarning, ConnectionPreview? preview, string? credential, QueryPreviewResult? testQueryResult) =>
        new(
            succeeded, connectMs, probeMs, serverVersion,
            error is null ? null : ConnectionDiagnostics.Redact(error, credential),
            libraryWarning,
            preview is null ? null : ConnectionDiagnostics.Redact(preview.ConnectionString, credential),
            preview?.JdbcUri is null ? null : ConnectionDiagnostics.Redact(preview.JdbcUri, credential),
            preview?.Properties.ToDictionary(kv => kv.Key, kv => ConnectionDiagnostics.Redact(kv.Value, credential)),
            testQueryResult);

    /// <summary>
    /// Phase 109j item 4: the deep, connection-scoped check — spawns
    /// <c>dbdatasync config library validate &lt;id&gt; --connection &lt;name&gt;</c> as a real child
    /// process (<see cref="LibraryValidationLauncher"/>) and waits for it. Separate from
    /// <see cref="Test"/> deliberately: this needs DDL rights on the target and costs a real process
    /// spawn plus a real scratch table create/write/drop, none of which belong on the cheap, frequent
    /// "Test" click.
    /// </summary>
    [HttpPost("{name}/validate-library")]
    public async Task<ActionResult<LibraryValidationReport>> ValidateLibrary(string name, CancellationToken cancellationToken)
    {
        ConnectionConfig connection;
        try
        {
            connection = configRepository.LoadConnection(name);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        if (!driverRegistry.TryGet(connection.DriverType, out var driver))
            return NotFound(new { error = $"No driver is registered for '{connection.DriverType}'." });

        if (driver.RequiredLibraryId is not { } libraryId)
        {
            return BadRequest(new
            {
                error = $"The '{connection.DriverType}' driver has no required library to validate " +
                    "(it resolves its provider by name, not through a compiled typed API).",
            });
        }

        var launched = await libraryValidationLauncher.RunAsync(libraryId, name, cancellationToken);
        return Ok(new LibraryValidationReport(launched.Succeeded, libraryId, launched.Output));
    }

    /// <summary>
    /// Which secret this connection resolves its credential through, and the environment variable
    /// SecretStore falls back to when no OS keyring is present. Read-only: there is one credential
    /// store, so there is nothing to choose — but an operator staring at an auth failure needs to know
    /// exactly which value the process is looking for, which is most of that diagnosis.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/credential-source")]
    public ActionResult<CredentialSource> GetCredentialSource(string name)
    {
        ConnectionConfig connection;
        try
        {
            connection = configRepository.LoadConnection(name);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        var secretRef = connection.CredentialSecretRef ?? SecretRefs.ForConnection(name);
        return Ok(new CredentialSource(
            Store: "Environment / OS keyring",
            secretRef,
            secrets.EnvName(secretRef),
            RequiresCredential: connection.AuthMode == AuthMode.SqlAuth));
    }

    [HttpPut("{name}")]
    public ActionResult<ConnectionConfig> Upsert(string name, [FromBody] ConnectionInput input)
    {
        input.Name = name;
        try
        {
            parameterCheck.ThrowIfInvalid(input);
            return Ok(configRepository.SaveConnection(input, currentUser.Author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Runs a query against this connection and returns its columns and first few rows.
    /// <para>
    /// **On the connection, not on the mapping**, because that is what the request actually needs: the
    /// query being previewed is the unsaved text in an editor, and the mapping it is destined for may
    /// not exist yet. Routing it through the mapping would make the mapping's saved config the subject
    /// and defeat the point — see <see cref="QueryPreviewRequest.Query"/>.
    /// </para>
    /// <para>
    /// Not authorized as a Viewer, deliberately, and the only endpoint on this controller that reads
    /// data rather than metadata: it runs SQL somebody typed, against a production system, with
    /// whatever the connection's own credential can reach. That is the same authority
    /// <c>POST /api/scripts/{name}/test</c> already needs, and it is held to the same bar rather than
    /// to the one that governs browsing a catalog.
    /// </para>
    /// <para>
    /// A query the engine rejects comes back as a result carrying its message, not as a 500 — an
    /// operator writing SQL is going to get it wrong several times on the way to right, and each of
    /// those is an answer rather than a fault.
    /// </para>
    /// </summary>
    [HttpPost("{name}/query-preview")]
    public async Task<ActionResult<QueryPreviewResult>> QueryPreview(
        string name, [FromBody] QueryPreviewBody body, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await testService.PreviewQueryAsync(
                new QueryPreviewRequest(name, body.Query, body.SampleRows), cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = $"Connection '{name}' does not exist." });
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteConnection(name, currentUser.Author);
        return NoContent();
    }
}

/// <param name="ConnectMs">Time to open the connection — usually the dominant cost, and the part that
/// fails when a host or port is wrong.</param>
/// <param name="ProbeMs">Time for the driver's own round trip once connected.</param>
/// <param name="LibraryWarning">Phase 109j: set only on a successful test, when the static
/// compatibility check found the installed library missing member(s) this driver uses — the
/// no-execution half's result surfacing through the flow an operator already checks. Null on any
/// failure (a connection that can't even connect has a more pressing problem) and whenever the check is
/// clean or has nothing to report yet (library not installed, driver has no
/// <see cref="Drivers.Abstractions.IDriver.RequiredLibraryId"/>).</param>
/// <param name="ResolvedConnectionString">Phase 176M — the ADO.NET-shaped connection string actually
/// resolved and attempted, redacted. Populated for every driver that implements
/// <see cref="IConnectionPreviewer"/> (every built-in and generic/descriptor driver today), null for one
/// that doesn't.</param>
/// <param name="JdbcUri">The resolved JDBC URL, redacted — null for a driver with no such notion (every
/// non-JDBC driver).</param>
/// <param name="OutsideProperties">Whatever reached the driver outside <paramref name="ResolvedConnectionString"/>/
/// <paramref name="JdbcUri"/> (JDBC's own <c>java.util.Properties</c> bag), each value redacted — always
/// empty for a plain ADO.NET driver today.</param>
/// <param name="TestQueryResult">
/// The connection's test query — its own <see cref="ConnectionConfig.TestQuery"/>, or the driver's
/// default — run and capped at 5 columns/20 rows for display, once <paramref name="Succeeded"/> is
/// true and a test query exists. Null when there's no test query to run, and also when the driver
/// itself has none registered (<see cref="DriverCapabilities.DefaultTestQuery"/> is null) and the
/// connection sets no override.
/// </param>
public sealed record ConnectionTestReport(
    bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error,
    string? LibraryWarning = null,
    string? ResolvedConnectionString = null,
    string? JdbcUri = null,
    IReadOnlyDictionary<string, string>? OutsideProperties = null,
    QueryPreviewResult? TestQueryResult = null);

public sealed record CredentialSource(string Store, string SecretRef, string EnvironmentVariable, bool RequiresCredential);

/// <summary>Phase 109j item 4's result — <paramref name="Output"/> is the spawned CLI child process's
/// own stdout (on success) or its own specific failure message (on stderr, or stdout if the process
/// never got as far as writing to stderr), verbatim.</summary>
public sealed record LibraryValidationReport(bool Succeeded, string LibraryId, string Output);

/// <summary>The body of a query preview. The connection is the route's, so it is not repeated here.</summary>
public sealed record QueryPreviewBody(string Query, int SampleRows = 20);