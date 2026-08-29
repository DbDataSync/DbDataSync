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
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long RowsRead,
    long RowsWritten,
    string? ErrorSummary,
    /// <summary>Why it failed, when that is something the product can act on. Null for the ordinary
    /// case — see <see cref="RunFailureKinds"/>.</summary>
    string? FailureKind = null);

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

public sealed record LogEntryRecord(
    long Id,
    Guid RunId,
    DateTimeOffset TimestampUtc,
    LogSeverity Level,
    string Message);
