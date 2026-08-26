using DataSync.Api.Models;
using DataSync.Api.Services;
using DataSync.State;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class RunsController(
    ProcessSupervisor supervisor,
    BackfillService backfillService,
    TaskRunStore taskRunStore,
    LogWriter logWriter) : ControllerBase
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

    /// <summary>
    /// Queues a reload of one table mapping. Deliberately a separate endpoint from the bodyless
    /// "Run Now" trigger above rather than a mode of it: different request shape, different scope (one
    /// mapping, not the replication), and different semantics — a backfill never advances the
    /// incremental watermark, so it can run alongside the replication's own schedule without
    /// disturbing it. Returns one RunId per segment, since each segment is scheduled independently.
    /// </summary>
    [HttpPost("replications/{name}/mappings/{mappingName}/backfill")]
    public async Task<IActionResult> Backfill(
        string name, string mappingName, [FromBody] BackfillRequest request, CancellationToken cancellationToken)
    {
        var result = await backfillService.EnqueueAsync(name, mappingName, request, cancellationToken);
        return result.Outcome switch
        {
            TriggerOutcome.Started => Accepted(new { runIds = result.RunIds }),
            TriggerOutcome.ReplicationNotFound => NotFound(new { error = "Replication or table mapping not found." }),
            TriggerOutcome.Invalid => BadRequest(new { error = result.Reason }),
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
