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
    BulkLoadService bulkLoadService,
    ReconcileService reconcileService,
    TaskRunStore taskRunStore,
    BulkLoadBatchStore bulkLoadBatchStore,
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
    /// mapping, not the replication), and different semantics — a bulk load never advances the
    /// incremental watermark, so it can run alongside the replication's own schedule without
    /// disturbing it. Returns one RunId per segment, since each segment is scheduled independently.
    /// </summary>
    [HttpPost("replications/{name}/mappings/{mappingName}/bulk-load")]
    public async Task<IActionResult> BulkLoad(
        string name, string mappingName, [FromBody] BulkLoadRequest request, CancellationToken cancellationToken)
    {
        var result = await bulkLoadService.EnqueueAsync(name, mappingName, request, cancellationToken);
        return result.Outcome switch
        {
            TriggerOutcome.Started => Accepted(new { runIds = result.RunIds }),
            TriggerOutcome.ReplicationNotFound => NotFound(new { error = "Replication or table mapping not found." }),
            TriggerOutcome.Invalid => BadRequest(new { error = result.Reason }),
            _ => StatusCode(500, new { error = result.Reason }),
        };
    }

    /// <summary>
    /// Queues a delete-diff sweep of one table mapping — phase 124. Same shape as <see cref="BulkLoad"/>
    /// (a separate endpoint from the bodyless trigger, one RunId per segment, never touches the
    /// incremental watermark), but always through the <c>KeyReconcile</c>/<c>KeyReconcileDelete</c>
    /// pair rather than an operator-chosen reader/writer — there is nothing else this action means.
    /// </summary>
    [HttpPost("replications/{name}/mappings/{mappingName}/reconcile-deletes")]
    public async Task<IActionResult> ReconcileDeletes(
        string name, string mappingName, [FromBody] ReconcileDeletesRequest request, CancellationToken cancellationToken)
    {
        var result = await reconcileService.EnqueueAsync(name, mappingName, request, cancellationToken);
        return result.Outcome switch
        {
            TriggerOutcome.Started => Accepted(new { runIds = result.RunIds }),
            TriggerOutcome.ReplicationNotFound => NotFound(new { error = "Replication or table mapping not found." }),
            TriggerOutcome.Invalid => BadRequest(new { error = result.Reason }),
            _ => StatusCode(500, new { error = result.Reason }),
        };
    }

    /// <summary>
    /// Recent bulk loads for one replication, each rolled up across its segment runs — the source for
    /// the Monitoring screen's "Batch reload" card. Newest first; the card reads only the first, the
    /// <paramref name="limit"/> is for a future Batch Load History view.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/bulk-loads")]
    public ActionResult<IReadOnlyList<BulkLoadBatchProgress>> BulkLoads(string name, [FromQuery] int limit = 5) =>
        Ok(bulkLoadBatchStore.GetRecentBulkLoads(name, Math.Clamp(limit, 1, 20)));

    /// <summary>
    /// The wider bulk-load history <c>GetRecentBulkLoads</c>' own doc comment named as a future
    /// screen — see phase 139. Real keyset pagination, mirroring phase 104's run history, plus an
    /// optional <paramref name="mappingName"/> filter; <see cref="BulkLoads"/> above stays exactly as
    /// it is — the Monitoring card's own "newest one, no cursor, no filter" contract does not change
    /// shape just because this second consumer exists.
    /// <para>
    /// <paramref name="cursor"/> is opaque and round-tripped verbatim from a previous call's
    /// <c>nextCursor</c> — see <see cref="BulkLoadHistoryCursorCodec"/> for why a cursor issued under a
    /// different <paramref name="mappingName"/> is treated the same as no cursor at all rather than
    /// rejected outright.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/bulk-loads/history")]
    public ActionResult<BulkLoadHistoryResponse> BulkLoadHistory(
        string name, [FromQuery] string? mappingName = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20)
    {
        var decodedCursor = BulkLoadHistoryCursorCodec.Decode(cursor, name, mappingName);
        var page = bulkLoadBatchStore.GetHistory(name, mappingName, decodedCursor, Math.Clamp(limit, 1, 20));
        var nextCursor = page.NextCursor is { } next
            ? BulkLoadHistoryCursorCodec.Encode(next, name, mappingName)
            : null;

        return Ok(new BulkLoadHistoryResponse(page.Batches, nextCursor));
    }

    /// <summary>
    /// What a segmenting strategy proposes for this mapping, right now — the Bulk Load form's
    /// checklist. Every candidate, selected or not: the operator is being shown a proposal to
    /// disagree with, not told what will happen.
    /// <para>
    /// A GET, and safe to call on picking a strategy, because a DuckDB one opens nothing. The other
    /// three do reach a real connection, which the form says out loud before offering them.
    /// </para>
    /// </summary>
    [HttpGet("replications/{name}/mappings/{mappingName}/segmenting/{strategyName}/preview")]
    public async Task<IActionResult> PreviewSegmenting(
        string name, string mappingName, string strategyName, [FromQuery] string? column,
        CancellationToken cancellationToken)
    {
        var result = await segmentingPreview.PreviewAsync(name, mappingName, strategyName, column, cancellationToken);
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
    /// above runs, so what the editor shows and what a bulk load later proposes cannot disagree.
    /// </para>
    /// <para>
    /// A mapping is still required, and not incidentally: a strategy proposes ranges over one table's
    /// column, and the source-SQL and target-SQL kinds resolve their connection through the mapping's
    /// endpoints. "Test this against nothing in particular" is not a question with an answer.
    /// </para>
    /// </summary>
    [HttpPost("replications/{name}/mappings/{mappingName}/segmenting/preview")]
    public async Task<IActionResult> PreviewUnsavedSegmenting(
        string name, string mappingName, [FromBody] TestSegmentingStrategyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await segmentingPreview.PreviewAsync(
            name, mappingName, request.Strategy, request.Column, cancellationToken);
        if (result.NotFound)
            return NotFound(new { error = "Replication or table mapping not found." });

        return result.Error is null
            ? Ok(new { candidates = result.Candidates })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Run history for one replication — filtered and paged since phase 104.
    /// <para>
    /// <c>cursor</c> is opaque and round-tripped verbatim from a previous call's <c>nextCursor</c> —
    /// see <see cref="RunHistoryCursorCodec"/> for what it carries and why a cursor issued under
    /// different filters is treated the same as no cursor at all rather than rejected outright.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runs")]
    public ActionResult<RunHistoryResponse> History(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] string? mappingName = null,
        [FromQuery] RunStatus? status = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var decodedCursor = RunHistoryCursorCodec.Decode(cursor, name, kind, mappingName, status);
        var page = taskRunStore.GetRunHistory(name, kind, mappingName, status, decodedCursor, limit);
        var nextCursor = page.NextCursor is { } next
            ? RunHistoryCursorCodec.Encode(next, name, kind, mappingName, status)
            : null;

        return Ok(new RunHistoryResponse(page, nextCursor));
    }

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
    /// Takes the same <paramref name="kind"/>, <paramref name="mappingName"/>, <paramref name="status"/>,
    /// <paramref name="cursor"/> and <paramref name="limit"/> as the history endpoint and resolves the
    /// same page (phase 104 added the last four alongside its own) — a client asking both questions
    /// about one screenful cannot be answered about two different sets of runs. Moving in lockstep like
    /// this matters more once history is filtered and paged, not less: a filtered, paged run with a
    /// watermark would otherwise show a blank watermark column for no reason a reader could see.
    /// </para>
    /// <para>
    /// Keyed by run id, and a run with no timestamp at all is simply absent: it aged out of
    /// <c>ChangeCheckHistory</c>'s window, or it never made a position durable in the first place —
    /// a bulk load, a verification, or a failed pass.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("replications/{name}/runs/watermark-times")]
    public ActionResult<IReadOnlyDictionary<Guid, RunWatermarkTimes>> WatermarkTimes(
        string name, [FromQuery] RunKind? kind = null, [FromQuery] string? mappingName = null,
        [FromQuery] RunStatus? status = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
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

        var decodedCursor = RunHistoryCursorCodec.Decode(cursor, name, kind, mappingName, status);
        var page = taskRunStore.GetRunHistory(name, kind, mappingName, status, decodedCursor, limit);
        return Ok(watermarkTimes.Describe(task, page));
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
    /// Asks for a full reload of this run's mapping: sets <see cref="ReadIntent.InitialLoad"/> and
    /// clears any <see cref="ReadHold"/>, so the next <c>Primary</c> pass reloads the table itself and
    /// the incremental pass after it can start again. See <see cref="ResyncService"/> — no longer only
    /// for a run whose source position expired; an operator can ask for this at any time.
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
