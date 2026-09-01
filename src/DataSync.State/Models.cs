namespace DataSync.State;

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
/// reload/backfill of one table mapping (Backfill — never touches the incremental watermark). See
/// architecture/implementation/done/phase-008-work-queue-schema.md.
/// </summary>
public enum RunKind
{
    Primary,
    Backfill,

    /// <summary>
    /// A comparison between a mapping's source and target — see phase 43. A run like any other, so it
    /// gets the same queue, lock, logs and history; what it produces is a file rather than rows at the
    /// target.
    /// </summary>
    Verification,
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
    /// position durable: a Backfill or Verification, or any failed run.
    /// </summary>
    string? PreviousWatermark = null,
    string? NewWatermark = null);

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
/// Failures with a specific remedy, as opposed to failures an operator has to go and read logs about.
/// Strings rather than an enum because this is stored and read back by clients that are not this
/// assembly, and a number would be meaningless in the database.
/// </summary>
public static class RunFailureKinds
{
    /// <summary>The source discarded the history the reader needed. The fix is a reload, which the UI
    /// offers as one click — see <c>PositionExpiredException</c>.</summary>
    public const string PositionExpired = "PositionExpired";
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
/// One pause or resume, as it happened. See <c>TaskRunStore.SetPaused</c>.
/// <para>
/// <see cref="Action"/> is a string rather than an enum for the same reason
/// <see cref="RunFailureKinds"/> is: it is stored, and read back by things that are not this assembly.
/// See <see cref="PauseActions"/>.
/// </para>
/// </summary>
/// <param name="Note">Whatever the operator typed for *this* action — including nothing, which is a
/// deliberate answer rather than a missing one, since the popup lets them clear it every time.</param>
public sealed record PauseEventRecord(
    long Id,
    string TaskName,
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
