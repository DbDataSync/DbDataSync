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
    string? ErrorSummary);

public sealed record LogEntryRecord(
    long Id,
    Guid RunId,
    DateTimeOffset TimestampUtc,
    LogSeverity Level,
    string Message);
