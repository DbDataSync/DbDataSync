using DbDataSync.State;
using DbDataSync.State.Remote;

namespace DbDataSync.Api.State;

/// <summary>
/// The state operations a spawned TaskRunner performs, served by the process that owns the file.
/// <para>
/// **Not a public API, and deliberately not a controller.** Controllers are discovered from the
/// assembly, so hosting them on the state server would put every other endpoint in this assembly on
/// the state port too. These routes are written out one by one because the complete list of what that
/// server exposes should be readable in one screen — see <see cref="StateHost"/>.
/// </para>
/// </summary>
public static class RunnerStateEndpoints
{
    public static void Map(IEndpointRouteBuilder app, LocalRunnerState state)
    {
        var group = app.MapGroup(StateProtocol.Route);

        // ---- Prerequisites ----

        // Deliberately outside the user scheme. This authenticates a *child process* with
        // RunnerToken, on a loopback-only listener — dragging it into a user scheme would mean a
        // spawned worker needing a user to exist, which is a worker that cannot run on a fresh
        // install. See RunnerStateGuard.
        group.AllowAnonymous();

        group.MapPost("/upsert-task", (UpsertTaskRequest r) =>
        {
            state.UpsertTask(r.TaskName, r.Enabled);
            return Results.Ok();
        });

        group.MapGet("/has-outstanding-work", (string taskName, RunLane lane) =>
            new BoolResponse(state.HasOutstandingWork(taskName, lane)));

        group.MapPost("/try-claim-next", (TryClaimNextRequest r) =>
            new WorkItemResponse(state.TryClaimNext(r.TaskName, r.WorkerId, r.Lane)));

        group.MapPost("/try-acquire-lock", (TryAcquireLockRequest r) =>
            new BoolResponse(state.TryAcquireLock(r.TaskName, r.RunKind, r.MappingName, r.RunId)));

        group.MapGet("/watermark", (string taskName, string mappingName, string sourceTable) =>
            new WatermarkResponse(state.GetWatermark(taskName, mappingName, sourceTable)));

        group.MapGet("/read-state", (string taskName, string mappingName, string sourceTable) =>
            new ReadStateResponse(state.GetReadState(taskName, mappingName, sourceTable)));

        group.MapPost("/begin-run", (BeginRunRequest r) =>
        {
            state.BeginRun(r.RunId, r.Pid);
            return Results.Ok();
        });

        // Phase 134: a Primary pass captured its own position ahead of an initial load and is handing
        // it to the state owner to take from here. Prerequisite, not Outcome — see
        // IRunnerState.RequestInitialLoad's own doc for why.
        group.MapPost("/request-initial-load", (RequestInitialLoadRequest r) =>
        {
            state.RequestInitialLoad(r.TaskName, r.MappingName, r.SourceTable, r.CapturedPosition, r.CapturedPositionTimeUtc);
            return Results.Ok();
        });

        // ---- Outcomes ----

        group.MapPost("/mark-running", (WorkItemRequest r) => { state.MarkRunning(r.WorkItemId); return Results.Ok(); });
        group.MapPost("/mark-done", (WorkItemRequest r) => { state.MarkDone(r.WorkItemId); return Results.Ok(); });
        group.MapPost("/mark-failed", (WorkItemRequest r) => { state.MarkFailed(r.WorkItemId); return Results.Ok(); });
        group.MapPost("/release-claim", (WorkItemRequest r) => { state.ReleaseClaim(r.WorkItemId); return Results.Ok(); });

        group.MapPost("/release-lock", (ReleaseLockRequest r) =>
        {
            state.ReleaseLock(r.TaskName, r.RunKind, r.MappingName);
            return Results.Ok();
        });

        group.MapPost("/complete-run", (CompleteRunRequest r) =>
        {
            state.CompleteRun(
                r.RunId, r.Status, r.RowsRead, r.RowsWritten, r.ErrorSummary, r.FailureKind, r.Timing,
                r.PreviousWatermark, r.NewWatermark, r.ErrorDetail);
            return Results.Ok();
        });

        group.MapPost("/set-watermark", (SetWatermarkRequest r) =>
        {
            // A request without a mapping comes from a runner older than phase 74. There is no mapping
            // to guess and no row it could safely land in, and the migration discarded whatever it
            // would have overwritten anyway — so it is refused rather than written somewhere wrong.
            if (r.MappingName is null)
                return Results.BadRequest("set-watermark requires a mapping name.");

            // The cached time rides along with no check of its own. Unlike the mapping name above
            // there is no wrong place for it to land: it is written to the row the watermark is
            // written to, and its absence is already a state a lag report answers for.
            state.SetWatermark(r.TaskName, r.MappingName, r.SourceTable, r.Watermark, r.WatermarkTimeUtc);
            return Results.Ok();
        });

        // Neither of these is called by anything yet — phase 100 wires the whole remote path through
        // before phase 101 has an intent transition or a hold to write with it. See JournalRecoveryTests.
        group.MapPost("/set-read-intent", (SetReadIntentRequest r) =>
        {
            // Same refusal as set-watermark, for the same reason: there is no mapping to guess and no
            // row it could safely land in.
            if (r.MappingName is null)
                return Results.BadRequest("set-read-intent requires a mapping name.");

            state.SetReadIntent(r.TaskName, r.MappingName, r.SourceTable, r.Intent);
            return Results.Ok();
        });

        group.MapPost("/set-read-hold", (SetReadHoldRequest r) =>
        {
            if (r.MappingName is null)
                return Results.BadRequest("set-read-hold requires a mapping name.");

            state.SetReadHold(r.TaskName, r.MappingName, r.SourceTable, r.Hold);
            return Results.Ok();
        });

        group.MapPost("/record-verification-result", (RecordVerificationResultRequest r) =>
        {
            state.RecordVerificationResult(r.Result);
            return Results.Ok();
        });

        // Not flushed here. LogWriter already batches on its own threshold and a two-second timer,
        // which is exactly what a runner writing in-process used to get; forcing a transaction per
        // arriving batch would make a remote runner's logs several times more expensive than a local
        // one's for no visible difference.
        group.MapPost("/log-batch", (LogBatchRequest r) =>
        {
            foreach (var entry in r.Entries)
                state.Log(entry.RunId, entry.Level, entry.Message);
            return Results.Ok();
        });
    }
}
