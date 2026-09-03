using DbDataSync.Core.Config;
using DbDataSync.Api.Models;
using DbDataSync.Api.Services;
using DbDataSync.State;
using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class RunsController(
    ProcessSupervisor supervisor,
    BackfillService backfillService,
    TaskRunStore taskRunStore,
    ResyncService resyncService,
    SegmentingPreviewService segmentingPreview,
    ConfigRepository configRepository,
    RunWatermarkTimeService watermarkTimes,
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

    /// <summary>
    /// What a segmenting strategy proposes for this mapping, right now — the Backfill form's
    /// checklist. Every candidate, selected or not: the operator is being shown a proposal to
    /// disagree with, not told what will happen.
    /// <para>
    /// A GET, and safe to call on picking a strategy, because a DuckDB one opens nothing. The other
    /// three do reach a real connection, which the form says out loud before offering them.
    /// </para>
    /// </summary>
    [HttpGet("replications/{name}/mappings/{mappingName}/segmenting/{strategyName}/preview")]
    public async Task<IActionResult> PreviewSegmenting(
        string name, string mappingName, string strategyName, CancellationToken cancellationToken)
    {
        var result = await segmentingPreview.PreviewAsync(name, mappingName, strategyName, cancellationToken);
        if (result.NotFound)
            return NotFound(new { error = "Replication, table mapping or segmenting strategy not found." });

        return result.Error is null
            ? Ok(new { candidates = result.Candidates })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// The same preview, for a strategy that is still being written — the editor's Test button (phase
    /// 61).
    /// <para>
    /// A POST rather than a GET only because the strategy travels in the body: a DuckDB query does not
    /// fit in a path segment. It writes nothing, and runs exactly the code the saved-strategy preview
    /// above runs, so what the editor shows and what a backfill later proposes cannot disagree.
    /// </para>
    /// <para>
    /// A mapping is still required, and not incidentally: a strategy proposes ranges over one table's
    /// column, and the source-SQL and target-SQL kinds resolve their connection through the mapping's
    /// endpoints. "Test this against nothing in particular" is not a question with an answer.
    /// </para>
    /// </summary>
    [HttpPost("replications/{name}/mappings/{mappingName}/segmenting/preview")]
    public async Task<IActionResult> PreviewUnsavedSegmenting(
        string name, string mappingName, [FromBody] SegmentingStrategyConfig strategy,
        CancellationToken cancellationToken)
    {
        var result = await segmentingPreview.PreviewAsync(name, mappingName, strategy, cancellationToken);
        if (result.NotFound)
            return NotFound(new { error = "Replication or table mapping not found." });

        return result.Error is null
            ? Ok(new { candidates = result.Candidates })
            : BadRequest(new { error = result.Error });
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runs")]
    public ActionResult<IReadOnlyList<TaskRunRecord>> History(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] int limit = 50) =>
        Ok(taskRunStore.GetRunHistory(name, kind, limit));

    /// <summary>
    /// When each of those runs' stored watermarks was the source's own position — see phase 88.
    /// <para>
    /// **A companion lookup rather than fields on the history above.** <c>TaskRunRecord</c> is the
    /// state store's own record of a run, written by the runner; these two timestamps are neither
    /// stored nor knowable at the moment a run ends — they are derived on read, out of polling
    /// history that has since been written and may since have been purged. Putting them on that
    /// record would make a durable row carry a value that changes as the retention window moves.
    /// </para>
    /// <para>
    /// Takes the same <paramref name="kind"/> and <paramref name="limit"/> as the history endpoint
    /// and resolves the same page, so a client asking both questions about one screenful cannot be
    /// answered about two different sets of runs.
    /// </para>
    /// <para>
    /// Keyed by run id, and a run with no timestamp at all is simply absent: it aged out of
    /// <c>ChangeCheckHistory</c>'s window, or it never made a position durable in the first place —
    /// a backfill, a verification, or a failed pass.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runs/watermark-times")]
    public ActionResult<IReadOnlyDictionary<Guid, RunWatermarkTimes>> WatermarkTimes(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] int limit = 50)
    {
        ReplicationTaskConfig task;
        try
        {
            task = configRepository.LoadReplicationTask(name);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        return Ok(watermarkTimes.Describe(task, taskRunStore.GetRunHistory(name, kind, limit)));
    }

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
