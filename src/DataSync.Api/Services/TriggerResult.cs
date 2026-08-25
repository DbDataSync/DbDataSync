namespace DataSync.Api.Services;

public enum TriggerOutcome
{
    Started,
    ReplicationNotFound,
    AlreadyRunning,
    FailedToStart,
}

public sealed record TriggerResult(TriggerOutcome Outcome, Guid? RunId = null, string? Reason = null)
{
    public static TriggerResult Started(Guid runId) => new(TriggerOutcome.Started, runId);
    public static TriggerResult NotFound() => new(TriggerOutcome.ReplicationNotFound, Reason: "Replication not found.");
    public static TriggerResult AlreadyRunning() => new(TriggerOutcome.AlreadyRunning, Reason: "A run is already in progress for this replication.");
    public static TriggerResult FailedToStart(string reason) => new(TriggerOutcome.FailedToStart, Reason: reason);
}
