using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
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

    [HttpPut("{name}")]
    public ActionResult<ConnectionConfig> Upsert(string name, [FromBody] ConnectionInput input)
    {
        input.Name = name;
        try
        {
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
