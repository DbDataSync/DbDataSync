using DataSync.Core.Config;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using DataSync.Api.Services;
using DataSync.Core.Git;
using DataSync.Core.Secrets;
using DataSync.Drivers.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    DriverConnectionFactory connectionFactory,
    ParameterCheck parameterCheck,
    GitAuthor author) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ConnectionConfig>> List() =>
        Ok(configRepository.ListConnections().Select(configRepository.LoadConnection).ToList());

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
    [HttpGet("~/api/drivers/{driverType}/capabilities")]
    public ActionResult<DriverCapabilities> CapabilitiesForDriver(ConnectionDriverType driverType)
    {
        var capabilities = driverRegistry.Describe(driverType);
        return capabilities is null
            ? NotFound(new { error = $"No driver is registered for '{driverType}'." })
            : Ok(capabilities);
    }

    /// <summary>
    /// Opens the connection and runs the driver's probe.
    /// <para>
    /// A failure is reported as <c>succeeded: false</c> with the provider's message, not as a 500: an
    /// unreachable database is an answer to the question this endpoint asks, and a 500 would tell the
    /// operator the console is broken rather than the database is.
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

        var started = Stopwatch.GetTimestamp();
        DbConnection? open = null;
        try
        {
            (open, _) = await connectionFactory.OpenAsync(name, cancellationToken);
            var connectMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var result = await tester.TestAsync(open, cancellationToken);
            return Ok(new ConnectionTestReport(
                result.Succeeded, connectMs, result.RoundTrip.TotalMilliseconds, result.ServerVersion, result.Error));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or SocketException)
        {
            // Failing to *open* is the most common failure and never reaches the driver's probe, so it
            // is reported in the same shape rather than as an error the SPA has to handle separately.
            return Ok(new ConnectionTestReport(
                Succeeded: false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, ServerVersion: null, ex.Message));
        }
        finally
        {
            if (open is not null)
                await open.DisposeAsync();
        }
    }

    /// <summary>
    /// Which secret this connection resolves its credential through, and the environment variable
    /// SecretStore falls back to when no OS keyring is present. Read-only: there is one credential
    /// store, so there is nothing to choose — but an operator staring at an auth failure needs to know
    /// exactly which value the process is looking for, which is most of that diagnosis.
    /// </summary>
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
            SecretRefs.EnvironmentVariableFor(secretRef),
            RequiresCredential: connection.AuthMode == AuthMode.SqlAuth));
    }

    [HttpPut("{name}")]
    public ActionResult<ConnectionConfig> Upsert(string name, [FromBody] ConnectionInput input)
    {
        input.Name = name;
        try
        {
            parameterCheck.ThrowIfInvalid(input);
            return Ok(configRepository.SaveConnection(input, author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteConnection(name, author);
        return NoContent();
    }
}

/// <param name="ConnectMs">Time to open the connection — usually the dominant cost, and the part that
/// fails when a host or port is wrong.</param>
/// <param name="ProbeMs">Time for the driver's own round trip once connected.</param>
public sealed record ConnectionTestReport(
    bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error);

public sealed record CredentialSource(string Store, string SecretRef, string EnvironmentVariable, bool RequiresCredential);