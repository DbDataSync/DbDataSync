using DbDataSync.Core.Config;

namespace DbDataSync.State.Remote;

/// <summary>
/// The wire contract between the state owner and the runners it spawns.
/// <para>
/// **Not a public API.** It is an internal channel between a parent process and its own children, on
/// loopback, authenticated by a token the parent generates per child. It is versioned only so a
/// mismatched pair fails loudly rather than subtly.
/// </para>
/// </summary>
public static class StateProtocol
{
    public const string Route = "/internal/state";

    /// <summary>Carries the per-child token. Named to make it obvious in a trace that this is not a
    /// user-facing credential.</summary>
    public const string TokenHeader = "X-DbDataSync-Runner-Token";

    /// <summary>The environment variable the parent passes the token in — **not** a command-line
    /// argument, because on Linux `/proc/&lt;pid&gt;/cmdline` is world-readable and
    /// `/proc/&lt;pid&gt;/environ` is not. A token on the command line would be visible to every local
    /// user through `ps`, which is precisely the threat it exists to answer.</summary>
    public const string TokenEnvironmentVariable = "DBDATASYNC_RUNNER_TOKEN";

    /// <summary>The environment variable carrying the loopback base address of the state endpoint.</summary>
    public const string EndpointEnvironmentVariable = "DBDATASYNC_STATE_ENDPOINT";
}

// ---- Requests. One record per operation, so the controller is a switch and not a parser. ----

public sealed record UpsertTaskRequest(string TaskName, bool Enabled);
public sealed record TryClaimNextRequest(string TaskName, string WorkerId);
public sealed record TryAcquireLockRequest(string TaskName, RunKind RunKind, string MappingName, Guid RunId);
public sealed record ReleaseLockRequest(string TaskName, RunKind RunKind, string MappingName);
public sealed record BeginRunRequest(Guid RunId, int? Pid);
public sealed record WorkItemRequest(long WorkItemId);
public sealed record CompleteRunRequest(
    Guid RunId, RunStatus Status, long RowsRead, long RowsWritten, string? ErrorSummary,
    /// <summary>Why it failed, when the product can act on it. Defaulted, so a journal written by an
    /// older runner still deserializes.</summary>
    string? FailureKind = null,
    /// <summary>Per-stage timing, for a mapping that opted into tracing. Defaulted for the same reason
    /// <see cref="FailureKind"/> is — a journal entry written before this existed has to replay.</summary>
    RunTiming? Timing = null,
    /// <summary>Where this run's watermark started and ended (phase 71). Defaulted for the same reason
    /// the two above are — a journal entry written before these existed has to replay.</summary>
    string? PreviousWatermark = null,
    string? NewWatermark = null,
    /// <summary>The full exception — type, message, stack trace, inner exceptions — for the Runs tab's
    /// failure popup. Defaulted for the same reason as the others above: a journal entry written
    /// before this existed still has to deserialize.</summary>
    string? ErrorDetail = null);
/// <param name="MappingName">Which mapping's position this is (phase 74). Defaulted, and nullable,
/// for the reason <see cref="CompleteRunRequest.FailureKind"/> is: an entry journalled before this
/// existed still has to deserialize. Recovery skips such an entry rather than inventing a mapping for
/// it — see <c>JournalRecovery</c>.</param>
/// <param name="WatermarkTimeUtc">When the source says <paramref name="Watermark"/> committed (phase
/// 87). Defaulted and nullable for the reason <see cref="MappingName"/> is — an entry journalled
/// before this existed still has to deserialize — but unlike that one it needs no recovery decision:
/// an entry without it replays as a watermark with no cached time, which is exactly what a lag report
/// already knows how to answer.</param>
public sealed record SetWatermarkRequest(
    string TaskName,
    string SourceTable,
    string Watermark,
    string? MappingName = null,
    DateTimeOffset? WatermarkTimeUtc = null);
public sealed record RecordVerificationResultRequest(VerificationResultRecord Result);

/// <summary>
/// A runner reporting the target shape its own provisioning just produced, for the owner to write into
/// the mapping's column cache (phase 94).
/// <para>
/// The one request here that is not a state operation. It rides this channel rather than one of its
/// own because the channel is about *how a child reaches its parent* — loopback, one token, one guard —
/// and none of that changes with what is being written; see <c>IRunnerConfig</c> for why the interface
/// it belongs to is nonetheless a separate one.
/// </para>
/// </summary>
public sealed record ReportProvisionedTargetColumnsRequest(
    string ReplicationName, string MappingName, IReadOnlyList<CachedColumn> Columns);
public sealed record LogRequest(Guid RunId, LogSeverity Level, string Message, DateTimeOffset TimestampUtc);

/// <summary>Log lines are batched: they are the highest-rate write here, and one HTTP round trip per
/// line is the difference between hundreds and tens of thousands per second.</summary>
public sealed record LogBatchRequest(IReadOnlyList<LogRequest> Entries);

public sealed record BoolResponse(bool Value);
public sealed record WatermarkResponse(string? Watermark);
public sealed record WorkItemResponse(WorkItem? Item);
