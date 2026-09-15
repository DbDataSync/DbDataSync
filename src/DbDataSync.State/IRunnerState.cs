using DbDataSync.Core.Config;

namespace DbDataSync.State;

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

    bool HasOutstandingWork(string taskName, RunLane lane);

    WorkItem? TryClaimNext(string taskName, string workerId, RunLane lane);

    bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId);

    string? GetWatermark(string taskName, string mappingName, string sourceTable);

    /// <summary>The intent, hold and position stored for one mapping, together — see
    /// <see cref="MappingReadState"/>. Null when the mapping has no row at all; nothing in this phase
    /// resolves that absence to a default, the same as <see cref="GetWatermark"/> answers null rather
    /// than inventing a full-load instruction.</summary>
    MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable);

    void BeginRun(Guid runId, int? pid);

    /// <summary>
    /// A <c>Primary</c> pass resolved its intent to <see cref="ReadIntent.InitialLoad"/> against a
    /// reader that can capture its own position without reading a row
    /// (<c>IPositionCapturing</c>) — see phase 134. The runner has already captured
    /// <paramref name="capturedPosition"/> itself, before touching the table, and hands it here so the
    /// state owner can take it from there: persist it as <c>Pending</c>, set
    /// <see cref="ReadHold.Loading"/>, and start the Bulk Load pipeline for this mapping — segmented
    /// exactly as an ordinary scheduled reload of it would be.
    /// <para>
    /// **Prerequisite, not Outcome — deliberately.** Every other member of this second group records
    /// work already done, which is safe to journal and replay at least once. This one *creates* new
    /// work (a fresh <c>BulkLoadBatchId</c> and its work-queue rows); replaying it blindly after a
    /// restart could double-enqueue a batch the owner already received. The safe retry is simply the
    /// next scheduled tick resolving to <see cref="ReadIntent.InitialLoad"/> again and asking again — a
    /// runner that cannot reach the owner for this either succeeds on a later attempt or learns the
    /// owner is gone (<c>StateOwnerUnavailableException</c>) and stops cleanly, the same as every
    /// other prerequisite here.
    /// </para>
    /// </summary>
    void RequestInitialLoad(
        string taskName, string mappingName, string sourceTable,
        string capturedPosition, DateTimeOffset? capturedPositionTimeUtc);

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

    /// <param name="previousWatermark">Where this run's watermark started, and where it ended — null
    /// for both unless the run made a new position durable (phase 71). Defaulted, so the failure paths
    /// and the supervisor's own completions say "no watermark change" by saying nothing.</param>
    void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null,
        RunTiming? timing = null,
        string? previousWatermark = null,
        string? newWatermark = null,
        string? errorDetail = null);

    /// <summary>
    /// Only ever called after the target write has committed — the watermark-on-success-only rule the
    /// whole run model rests on. That is what makes it safe to journal: its presence in a journal is
    /// itself the evidence the write succeeded.
    /// </summary>
    /// <param name="watermarkTimeUtc">When the source says <paramref name="watermark"/> committed,
    /// from the reader that just produced it (phase 87). Defaulted, because most readers have no such
    /// mapping and none is required to have one — a null costs a lag figure, never the position.</param>
    void SetWatermark(
        string taskName,
        string mappingName,
        string sourceTable,
        string watermark,
        DateTimeOffset? watermarkTimeUtc = null);

    /// <summary>
    /// What the next pass over this mapping is meant to do, from here on — written wherever a pass
    /// transitions its own intent (phase 101's <c>ChangesFromEarliest</c> → <c>Changes</c>, say).
    /// **Nothing writes this yet**: phase 100 wires it the whole way through so the plumbing exists
    /// before phase 101 has anything of its own to say with it.
    /// </summary>
    void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent);

    /// <summary>Why this mapping's next <c>Primary</c> pass should not run — set independently of
    /// <see cref="SetReadIntent"/> for the reason <see cref="ReadHold"/>'s own doc gives: a hold must
    /// survive underneath whatever intent it was entered with. **Nothing writes this yet** — see
    /// <see cref="SetReadIntent"/>.</summary>
    void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold);

    /// <summary>
    /// Where a verification result was written. An outcome like any other — the work is already done
    /// and the file is already on disk, so losing this would leave a result nobody can find.
    /// </summary>
    void RecordVerificationResult(VerificationResultRecord result);

    void Log(Guid runId, LogSeverity level, string message);

    void Flush();
}
