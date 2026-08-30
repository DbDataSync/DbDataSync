using DataSync.Api.Models;
using DataSync.Api.Services;
using DataSync.State;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class RunsController(
    ProcessSupervisor supervisor,
    BackfillService backfillService,
    TaskRunStore taskRunStore,
    ResyncService resyncService,
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

    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runs")]
    public ActionResult<IReadOnlyList<TaskRunRecord>> History(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] int limit = 50) =>
        Ok(taskRunStore.GetRunHistory(name, kind, limit));

    [Authorize(Policies.Viewer)]
    [HttpGet("runs/{runId:guid}")]
    public ActionResult<TaskRunRecord> Get(Guid runId)
    {
        var run = taskRunStore.GetRun(runId);
        return run is null ? NotFound() : Ok(run);
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("runs/{runId:guid}/logs")]
    public ActionResult<IReadOnlyList<LogEntryRecord>> Logs(Guid runId, [FromQuery] long? sinceId = null) =>
        Ok(logWriter.GetLogs(runId, sinceId));

    /// <summary>
    /// The recovery for a run whose source position expired: reload the table, and clear the stored
    /// watermark so the incremental pass can start again.
    /// <para>
    /// Offered rather than performed, which is why it is an endpoint and not something the runner does
    /// on its own — a full reload of a table that fell behind can be hours of work, and nobody asked
    /// for it just because a pass failed.
    /// </para>
    /// </summary>
    [HttpPost("runs/{runId:guid}/resync")]
    public async Task<IActionResult> Resync(Guid runId, CancellationToken cancellationToken)
    {
        var result = await resyncService.ResyncAsync(runId, cancellationToken);
        return result.Outcome switch
        {
            TriggerOutcome.ReplicationNotFound => NotFound(),
            TriggerOutcome.Invalid => BadRequest(new { error = result.Reason }),
            TriggerOutcome.FailedToStart => StatusCode(500, new { error = result.Reason }),
            _ => Accepted(new { runIds = result.RunIds }),
        };
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public IActionResult Cancel(Guid runId) =>
        supervisor.CancelRun(runId) ? Ok() : NotFound(new { error = "No cancellable run with that id on this API instance." });
}
