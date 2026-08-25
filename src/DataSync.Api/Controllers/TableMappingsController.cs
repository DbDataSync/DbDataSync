using DataSync.Core.Config;
using DataSync.Core.Git;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications/{replicationName}/table-mappings")]
public sealed class TableMappingsController(ConfigRepository configRepository, GitAuthor author) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List(string replicationName) =>
        Ok(configRepository.ListTableMappings(replicationName));

    [HttpGet("{mappingName}")]
    public ActionResult<TableMappingConfig> Get(string replicationName, string mappingName)
    {
        try
        {
            return Ok(configRepository.LoadTableMapping(replicationName, mappingName));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{mappingName}")]
    public ActionResult<TableMappingConfig> Upsert(string replicationName, string mappingName, [FromBody] TableMappingConfig mapping)
    {
        mapping.Name = mappingName;
        try
        {
            return Ok(configRepository.SaveTableMapping(replicationName, mapping, author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{mappingName}")]
    public IActionResult Delete(string replicationName, string mappingName)
    {
        configRepository.DeleteTableMapping(replicationName, mappingName, author);
        return NoContent();
    }
}
