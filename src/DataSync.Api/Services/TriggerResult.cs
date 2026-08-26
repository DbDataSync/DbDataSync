namespace DataSync.Api.Services;

public enum TriggerOutcome
{
    Started,
    ReplicationNotFound,
    FailedToStart,
}

/// <summary>
/// A "Run Now" trigger now enqueues a Primary pass per table mapping rather than starting one run for
/// the whole replication — see architecture/implementation/done/phase-8-work-queue-schema.md. There is no
/// AlreadyRunning rejection anymore: enqueueing is idempotent per (task, kind, mapping) — a mapping
/// that's already queued or in flight is silently skipped (WorkQueueStore.Enqueue returns its
/// existing RunId), not treated as a whole-trigger failure the way one shared lock used to be.
/// </summary>
public sealed record TriggerResult(TriggerOutcome Outcome, IReadOnlyList<Guid>? RunIds = null, string? Reason = null)
{
    public static TriggerResult Started(IReadOnlyList<Guid> runIds) => new(TriggerOutcome.Started, runIds);
    public static TriggerResult NotFound() => new(TriggerOutcome.ReplicationNotFound, Reason: "Replication not found.");
    public static TriggerResult FailedToStart(string reason) => new(TriggerOutcome.FailedToStart, Reason: reason);
}
