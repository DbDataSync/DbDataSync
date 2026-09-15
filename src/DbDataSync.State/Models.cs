using System.Collections;

namespace DbDataSync.State;

public enum RunStatus
{
    Queued,
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Distinguishes a replication's ongoing incremental sync (Primary — one per table mapping, driven by
/// its own schedule, the only kind that ever advances a ChangeWatermarks row) from an on-demand
/// reload/bulk-load of one table mapping (BulkLoad — never touches the incremental watermark). See
/// architecture/implementation/done/phase-008-work-queue-schema.md.
/// </summary>
public enum RunKind
{
    Primary,
    BulkLoad,

    /// <summary>
    /// A comparison between a mapping's source and target — see phase 43. A run like any other, so it
    /// gets the same queue, lock, logs and history; what it produces is a file rather than rows at the
    /// target.
    /// </summary>
    Verification,

    /// <summary>
    /// A key-diff delete sweep — see phase 124 (<c>architecture/planning/done/watermark-delete-detection.md</c>).
    /// Reads only a segment's source primary-key values and deletes target rows whose key is absent
    /// from that set; never inserts or updates and never advances the incremental watermark, the same
    /// posture <see cref="BulkLoad"/> already has. On-demand in this phase, and — from phase 125 — also
    /// scheduled.
    /// </summary>
    ReconcileDeletes,
}

/// <summary>
/// The two lanes a replication's worker process drains in parallel, each with its own bounded channel,
/// its own pool of consumers and its own configured degree of parallelism — so a long-running reload
/// can never occupy a slot an incremental pass needs. See
/// <c>DbDataSync.Core.Config.ChangeProcessingConfig</c> and phase-108.
/// </summary>
public enum RunLane
{
    /// <summary>Ongoing incremental sync — <see cref="RunKind.Primary"/> only. The latency-sensitive
    /// lane: its passes are scheduled and an operator watching lag expects them to keep up.</summary>
    ChangeProcessing,

    /// <summary>On-demand, non-incremental work — <see cref="RunKind.BulkLoad"/>,
    /// <see cref="RunKind.Verification"/> and <see cref="RunKind.ReconcileDeletes"/>. All three read
    /// whole tables (or a segment of one) and can run for a long time; keeping them off the
    /// change-processing lane is the point of the split.</summary>
    BulkLoad,
}

/// <summary>Which <see cref="RunKind"/>s belong to which <see cref="RunLane"/> — the one place the
/// mapping is stated, so the claim query and the worker cannot disagree about it.</summary>
public static class RunLanes
{
    public static IReadOnlyList<RunKind> KindsFor(RunLane lane) => lane switch
    {
        RunLane.ChangeProcessing => [RunKind.Primary],
        RunLane.BulkLoad => [RunKind.BulkLoad, RunKind.Verification, RunKind.ReconcileDeletes],
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null),
    };

    public static RunLane LaneFor(RunKind kind) =>
        kind == RunKind.Primary ? RunLane.ChangeProcessing : RunLane.BulkLoad;
}

// Deliberately not named LogLevel — avoids ambiguity wherever this is used alongside
// Microsoft.Extensions.Logging.LogLevel (the API/TaskRunner hosts, later phases).
public enum LogSeverity
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record TaskRunRecord(
    Guid RunId,
    string TaskName,
    int? Pid,
    RunStatus Status,
    RunKind RunKind,
    string MappingName,
    string? SegmentLabel,
    /// <summary>
    /// When the work was queued — written by <c>WorkQueueStore.Enqueue</c>, before any worker exists
    /// to do it. The one timestamp every run has, since the row is created by the enqueue.
    /// <para>
    /// Null only for a row written before phase 73's migration ran, and that migration backfills it
    /// from the old <c>StartedAtUtc</c>, which held exactly this value under the wrong name — so in
    /// practice it is never null.
    /// </para>
    /// </summary>
    DateTimeOffset? EnqueuedAtUtc,
    /// <summary>
    /// When a worker took this item off the queue — written by <c>WorkQueueStore.TryClaimNext</c>, in
    /// the same transaction as the <c>WorkQueue</c> row's own claim, so the two tables cannot disagree
    /// about whether a claim happened.
    /// <para>
    /// Null for a run nobody ever claimed — still queued, or cancelled first — and for rows predating
    /// phase 72's column, which is not backfilled because nothing ever recorded the moment.
    /// </para>
    /// </summary>
    DateTimeOffset? ClaimedAtUtc,
    /// <summary>
    /// When the worker began executing it — written by <c>TaskRunStore.BeginRun</c>. The moment the run
    /// itself starts, and the boundary between the two figures the product reports: queue time is
    /// <c>StartedAtUtc - EnqueuedAtUtc</c> and processing time is <c>EndedAtUtc - StartedAtUtc</c>.
    /// <para>
    /// All four timestamps are exposed raw rather than as precomputed deltas, matching phase 71's
    /// watermark pair — one rule for this record rather than two.
    /// </para>
    /// <para>
    /// Null for a run that never reached <c>Running</c>, and for every row predating phase 73: this
    /// column held the enqueue time before then, and that value now lives in
    /// <see cref="EnqueuedAtUtc"/> rather than being left here to misreport a start.
    /// </para>
    /// </summary>
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long RowsRead,
    long RowsWritten,
    string? ErrorSummary,
    /// <summary>Why it failed, when that is something the product can act on. Null for the ordinary
    /// case — see <see cref="RunFailureKinds"/>.</summary>
    string? FailureKind = null,
    /// <summary>
    /// What this pass actually did, and how long each stage took — null unless the mapping opted into
    /// tracing (phase 59).
    /// <para>
    /// The Kinds are recorded alongside the timings deliberately: a unit of work may override the
    /// replication's configured pipeline, so "which reader produced this number" is not answerable
    /// from the replication's config afterwards.
    /// </para>
    /// </summary>
    RunTiming? Timing = null,
    /// <summary>
    /// Where this run's watermark started and where it ended — the history behind
    /// <c>ChangeWatermarks</c>' single current value (phase 71). Both null for a run that made no new
    /// position durable: a BulkLoad or Verification, or any failed run.
    /// </summary>
    string? PreviousWatermark = null,
    string? NewWatermark = null,
    /// <summary>
    /// The full exception a failed run raised — type, message, stack trace, and every inner
    /// exception's own — for the Runs tab's failure popup. <see cref="ErrorSummary"/> stays the short
    /// one-liner used everywhere space is tight; this is the separate, longer field for the one place
    /// that wants the whole picture. Null on success, and null for a run recorded before this field
    /// existed or one whose failure path never had an exception object to hand it, in which case the
    /// popup falls back to <see cref="ErrorSummary"/>.
    /// </summary>
    string? ErrorDetail = null);

/// <param name="ReaderTimeToFirstRowMs">
/// From just before <c>ReadChangesAsync</c> to the first row arriving. A prefix of
/// <paramref name="ReaderLifetimeMs"/>, always.
/// </param>
/// <param name="ReaderLifetimeMs">From the same start to the row stream being disposed.</param>
/// <param name="StagingDurationMs">
/// The whole <c>StageAsync</c> call. Close to the reader's lifetime for a provider that writes
/// straight through as rows arrive, and genuinely longer for one that does work *after* the stream is
/// exhausted — a file-based provider moving or uploading what it staged. The difference between the
/// two is that provider's own work beyond consuming the source, which is why both are recorded.
/// </param>
public sealed record RunTiming(
    string? ReaderKind = null,
    long? ReaderTimeToFirstRowMs = null,
    long? ReaderLifetimeMs = null,
    string? StagingKind = null,
    long? StagingDurationMs = null,
    string? WriterKind = null,
    long? WriterDurationMs = null);

/// <summary>
/// A position in <c>TaskRunStore.GetRunHistory</c>'s keyset — see phase 104.
/// <para>
/// <c>(EnqueuedAtUtc, RunId)</c>, not an offset: the history is append-heavy and polled, so an offset
/// would shift under a page as new runs land at the top, duplicating some rows and skipping others.
/// <c>RunId</c> is the tie-break for the (rare, but real for a batch enqueued in one instant) case
/// where two rows share the same <c>EnqueuedAtUtc</c> — without it, ties would page unpredictably
/// depending on whichever order the engine happened to return them in.
/// </para>
/// </summary>
public readonly record struct RunHistoryCursor(DateTimeOffset EnqueuedAtUtc, Guid RunId);

/// <summary>
/// One page of <c>GetRunHistory</c>, and where the next one starts.
/// <para>
/// **Implements <see cref="IReadOnlyList{TaskRunRecord}"/> itself, rather than exposing a <c>Runs</c>
/// list property**, so every caller that only ever wanted "the runs for this task" — which is every
/// caller but the history endpoint itself, including a long list of existing tests written before
/// paging existed — keeps compiling and behaving exactly as it did, unaware a next page exists at
/// all. Only the endpoint that hands a cursor back to a client reads <see cref="NextCursor"/>.
/// </para>
/// </summary>
public sealed class RunHistoryPage(IReadOnlyList<TaskRunRecord> runs, RunHistoryCursor? nextCursor)
    : IReadOnlyList<TaskRunRecord>
{
    /// <summary>Where the next page starts, or null when this page reached the end of the history —
    /// never a cursor that would loop back to the first page.</summary>
    public RunHistoryCursor? NextCursor { get; } = nextCursor;

    public int Count => runs.Count;
    public TaskRunRecord this[int index] => runs[index];
    public IEnumerator<TaskRunRecord> GetEnumerator() => runs.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Failures with a specific remedy, as opposed to failures an operator has to go and read logs about.
/// Strings rather than an enum because this is stored and read back by clients that are not this
/// assembly, and a number would be meaningless in the database.
/// </summary>
public static class RunFailureKinds
{
    /// <summary>The source discarded the history the reader needed. The fix is a reload, which the UI
    /// offers as one click — see <c>PositionExpiredException</c>.</summary>
    public const string PositionExpired = "PositionExpired";

    /// <summary>
    /// A reader, writer or staging provider needed a column's cached shape and the mapping's metadata
    /// cache (phase 90) didn't have it — see <c>MetadataNotCachedException</c>. The fix is Refresh
    /// metadata on the mapping, not a reload: nothing about the *position* is wrong, only the picture of
    /// the table's shape this pass needed to run at all — phase 91.
    /// </summary>
    public const string MetadataNotCached = "MetadataNotCached";

    /// <summary>A mapping's own auto-triggered initial load lost its race against other in-flight work
    /// for the same mapping+segment (an operator's own concurrent reload, most likely) — see
    /// <c>WorkQueueCollisionException</c>. Unlike the two kinds above, the fix is not an operator action:
    /// the mapping's own next scheduled pass retries this on its own once the winner finishes — phase
    /// 143.</summary>
    public const string ConcurrentLoadInProgress = "ConcurrentLoadInProgress";
}

/// <param name="ResultPath">Where the parquet is. The index says where; the file says what.</param>
/// <param name="SourceReadAtUtc">When each side was read. The gap between them is what tells an
/// operator whether a difference is drift or a defect.</param>
public sealed record VerificationResultRecord(
    long Id,
    Guid RunId,
    string TaskName,
    string MappingName,
    string CheckName,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset SourceReadAtUtc,
    DateTimeOffset TargetReadAtUtc,
    int GroupsCompared,
    int DifferingGroups,
    string ResultPath);

/// <summary>
/// One pause or resume, as it happened. See <c>TaskRunStore.SetPaused</c> (the replication grain) and
/// <c>TaskRunStore.SetMappingHold</c> (the table-mapping grain, phase 131).
/// <para>
/// <see cref="Action"/> is a string rather than an enum for the same reason
/// <see cref="RunFailureKinds"/> is: it is stored, and read back by things that are not this assembly.
/// See <see cref="PauseActions"/>.
/// </para>
/// </summary>
/// <param name="MappingName">Null for the replication grain — every row phase 64 ever wrote, and every
/// row <c>SetPaused</c> still writes. Set for the table-mapping grain phase 131 adds:
/// <c>SetMappingHold</c>'s own action against one mapping's <c>ReadHold</c>.</param>
/// <param name="Note">Whatever the operator typed for *this* action — including nothing, which is a
/// deliberate answer rather than a missing one, since the popup lets them clear it every time.</param>
public sealed record PauseEventRecord(
    long Id,
    string TaskName,
    string? MappingName,
    string Action,
    string? Note,
    DateTimeOffset PerformedAtUtc,
    string PerformedBy);

/// <summary>The two things that can happen to a pause.</summary>
public static class PauseActions
{
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
}

public sealed record LogEntryRecord(
    long Id,
    Guid RunId,
    DateTimeOffset TimestampUtc,
    LogSeverity Level,
    string Message);
