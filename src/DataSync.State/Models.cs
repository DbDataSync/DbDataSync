namespace DataSync.State;

public enum RunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
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
