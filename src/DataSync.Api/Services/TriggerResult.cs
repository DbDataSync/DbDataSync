namespace DataSync.Api.Services;

public enum TriggerOutcome
{
    Started,
    ReplicationNotFound,
    FailedToStart,
    Invalid,
}

/// <summary>
/// A "Run Now" trigger now enqueues a Primary pass per table mapping rather than starting one run for
/// the whole replication — see architecture/implementation/done/phase-008-work-queue-schema.md. There is no
/// AlreadyRunning rejection anymore: enqueueing is idempotent per (task, kind, mapping) — a mapping
/// that's already queued or in flight is silently skipped (WorkQueueStore.Enqueue returns its
/// existing RunId), not treated as a whole-trigger failure the way one shared lock used to be.
/// </summary>
public sealed record TriggerResult(TriggerOutcome Outcome, IReadOnlyList<Guid>? RunIds = null, string? Reason = null)
{
    public static TriggerResult Started(IReadOnlyList<Guid> runIds) => new(TriggerOutcome.Started, runIds);
    public static TriggerResult NotFound() => new(TriggerOutcome.ReplicationNotFound, Reason: "Replication not found.");
    public static TriggerResult FailedToStart(string reason) => new(TriggerOutcome.FailedToStart, Reason: reason);

    /// <summary>The request itself can't produce runnable work — an empty segment list, a segment
    /// column that doesn't exist, a reader that can't do what was asked of it. Rejected up front
    /// rather than queued and left to fail once a worker claims it, since the caller is right there
    /// to be told.</summary>
    public static TriggerResult Invalid(string reason) => new(TriggerOutcome.Invalid, Reason: reason);
}
