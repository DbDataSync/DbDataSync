using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications")]
public sealed class ReplicationsController(
    ConfigRepository configRepository,
    ParameterCheck parameterCheck,
    ProcessSupervisor supervisor,
    CurrentUser currentUser) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List() => Ok(configRepository.ListReplications());

    [Authorize(Policies.Viewer)]
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

    /// <summary>
    /// What this replication's worker process is doing, right now.
    /// <para>
    /// Pulled rather than pushed, on <c>run-metrics.md</c>'s call: a status card is not something
    /// anybody stares at, and the case where somebody is watching is a live run, which the run hub
    /// already covers.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/status")]
    public ActionResult<ReplicationStatus> Status(string name)
    {
        if (!configRepository.ListReplications().Contains(name, StringComparer.Ordinal))
            return NotFound();

        return Ok(supervisor.DescribeStatus(name));
    }

    /// <summary>
    /// Turns a replication on or off, and nothing else.
    /// <para>
    /// Its own endpoint rather than a mode of the upsert, because it is the one field that commits on
    /// its own. Routing it through the full save would mean toggling Enabled from the Runs tab quietly
    /// committed whatever half-finished pipeline edit was sitting in the Overview's draft — a change
    /// nobody asked for, made by a control that says nothing about it.
    /// </para>
    /// </summary>
    [HttpPut("{name}/enabled")]
    public ActionResult<ReplicationTaskConfig> SetEnabled(string name, [FromBody] SetEnabledRequest request)
    {
        try
        {
            var task = configRepository.LoadReplicationTask(name);
            task.Enabled = request.Enabled;
            return Ok(configRepository.SaveReplicationTask(task, currentUser.Author));
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
            parameterCheck.ThrowIfInvalid(task);
            return Ok(configRepository.SaveReplicationTask(task, currentUser.Author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteReplicationTask(name, currentUser.Author);
        return NoContent();
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/history")]
    public ActionResult<IReadOnlyList<CommitInfo>> History(string name, [FromQuery] int limit = 50) =>
        Ok(configRepository.GetReplicationHistory(name, limit));
}

/// <summary>The one field that saves on its own — see <c>SetEnabled</c>.</summary>
public sealed record SetEnabledRequest(bool Enabled);
