using DataSync.Api.Services;
using DataSync.State;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class RunsController(ProcessSupervisor supervisor, TaskRunStore taskRunStore, LogWriter logWriter) : ControllerBase
{
    [HttpPost("replications/{name}/runs")]
    public IActionResult Trigger(string name)
    {
        var result = supervisor.TriggerReplication(name);
        return result.Outcome switch
        {
            TriggerOutcome.Started => Accepted(new { runIds = result.RunIds }),
            TriggerOutcome.ReplicationNotFound => NotFound(new { error = result.Reason }),
            _ => StatusCode(500, new { error = result.Reason }),
        };
    }

    [HttpGet("replications/{name}/runs")]
    public ActionResult<IReadOnlyList<TaskRunRecord>> History(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] int limit = 50) =>
        Ok(taskRunStore.GetRunHistory(name, kind, limit));

    [HttpGet("runs/{runId:guid}")]
    public ActionResult<TaskRunRecord> Get(Guid runId)
    {
        var run = taskRunStore.GetRun(runId);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpGet("runs/{runId:guid}/logs")]
    public ActionResult<IReadOnlyList<LogEntryRecord>> Logs(Guid runId, [FromQuery] long? sinceId = null) =>
        Ok(logWriter.GetLogs(runId, sinceId));

    [HttpPost("runs/{runId:guid}/cancel")]
    public IActionResult Cancel(Guid runId) =>
        supervisor.CancelRun(runId) ? Ok() : NotFound(new { error = "No cancellable run with that id on this API instance." });
}
