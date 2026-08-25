using DataSync.Core.Config;
using DataSync.Core.Git;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController(ConfigRepository configRepository, GitAuthor author) : ControllerBase
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
