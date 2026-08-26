using DataSync.Core.Config;
using DataSync.Core.Git;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications")]
public sealed class ReplicationsController(ConfigRepository configRepository, GitAuthor author) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List() => Ok(configRepository.ListReplications());

    [HttpGet("{name}")]
    public ActionResult<ReplicationTaskConfig> Get(string name)
    {
        try
        {
            return Ok(configRepository.LoadReplicationTask(name));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{name}")]
    public ActionResult<ReplicationTaskConfig> Upsert(string name, [FromBody] ReplicationTaskConfig task)
    {
        task.Name = name;
        try
        {
            return Ok(configRepository.SaveReplicationTask(task, author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteReplicationTask(name, author);
        return NoContent();
    }

    [HttpGet("{name}/history")]
    public ActionResult<IReadOnlyList<CommitInfo>> History(string name, [FromQuery] int limit = 50) =>
        Ok(configRepository.GetReplicationHistory(name, limit));
}
