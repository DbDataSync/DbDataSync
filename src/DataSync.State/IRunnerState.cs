namespace DataSync.State;

/// <summary>
/// Every state operation a TaskRunner performs — and the whole boundary between a run and the state
/// store.
/// <para>
/// One interface rather than one per store, because the boundary that matters is "what a runner does
/// to state", not "which table it lands in". Fifteen methods across five stores collapse to one
/// surface with one remote implementation and one controller behind it, and the split between the two
/// halves below is what makes the offline journal expressible at all.
/// </para>
/// <para>
/// The API keeps direct access to the stores; it *is* the owning process. This exists for the child
/// processes it spawns — see phase 39.
/// </para>
/// </summary>
public interface IRunnerState
{
    // ---- Prerequisites: reads and acquisitions ----
    //
    // These happen *before* work, and a runner that cannot perform them cannot proceed. They are never
    // journalled: there is no outcome to record, and inventing one would mean claiming work or a lock
    // the owner never granted.

    void UpsertTask(string taskName, bool enabled);

    bool HasOutstandingWork(string taskName);

    WorkItem? TryClaimNext(string taskName, string workerId);

    bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId);

    string? GetWatermark(string taskName, string sourceTable);

    void BeginRun(Guid runId, int? pid);

    // ---- Outcomes: what happened ----
    //
    // These record work that has already been done, so a runner that cannot deliver them has something
    // real to preserve. Every one of them is journallable — see IStateJournal.

    void MarkRunning(long workItemId);

    void MarkDone(long workItemId);

    void MarkFailed(long workItemId);

    /// <summary>Returns a claimed-but-unfinished item to the queue. Journalled as *released*, never as
    /// completed, so the work is re-claimed rather than silently dropped.</summary>
    void ReleaseClaim(long workItemId);

    void ReleaseLock(string taskName, RunKind runKind, string mappingName);

    void CompleteRun(Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary);

    /// <summary>
    /// Only ever called after the target write has committed — the watermark-on-success-only rule the
    /// whole run model rests on. That is what makes it safe to journal: its presence in a journal is
    /// itself the evidence the write succeeded.
    /// </summary>
    void SetWatermark(string taskName, string sourceTable, string watermark);

    void Log(Guid runId, LogSeverity level, string message);

    void Flush();
}
