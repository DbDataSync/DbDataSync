using DataSync.State;
using DataSync.State.Remote;

namespace DataSync.Api.State;

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

        group.MapGet("/has-outstanding-work", (string taskName) =>
            new BoolResponse(state.HasOutstandingWork(taskName)));

        group.MapPost("/try-claim-next", (TryClaimNextRequest r) =>
            new WorkItemResponse(state.TryClaimNext(r.TaskName, r.WorkerId)));

        group.MapPost("/try-acquire-lock", (TryAcquireLockRequest r) =>
            new BoolResponse(state.TryAcquireLock(r.TaskName, r.RunKind, r.MappingName, r.RunId)));

        group.MapGet("/watermark", (string taskName, string sourceTable) =>
            new WatermarkResponse(state.GetWatermark(taskName, sourceTable)));

        group.MapPost("/begin-run", (BeginRunRequest r) =>
        {
            state.BeginRun(r.RunId, r.Pid);
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
            state.CompleteRun(r.RunId, r.Status, r.RowsRead, r.RowsWritten, r.ErrorSummary, r.FailureKind);
            return Results.Ok();
        });

        group.MapPost("/set-watermark", (SetWatermarkRequest r) =>
        {
            state.SetWatermark(r.TaskName, r.SourceTable, r.Watermark);
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
