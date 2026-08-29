using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.State.Remote;

/// <summary>Raised when the owner is gone and the runner has spilled what it could. The caller's job is
/// to stop cleanly, not to retry.</summary>
public sealed class StateOwnerUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// <see cref="IRunnerState"/> over HTTP to the process that owns the state file.
/// <para>
/// This is where phase 39's two halves become behaviour. A **prerequisite** that cannot reach the owner
/// fails, because there is nothing to preserve and proceeding would mean assuming a claim nobody
/// granted. An **outcome** that cannot reach the owner is written to a <see cref="StateJournal"/>, so
/// work already done is not lost with the process that did it.
/// </para>
/// </summary>
public sealed class RemoteRunnerState : IRunnerState, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly StateJournal _journal;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _retryDelay;
    private readonly Action<string> _report;
    private readonly List<LogRequest> _pendingLogs = [];
    private readonly Lock _gate = new();

    /// <summary>Set once the owner has been declared gone. Everything after this point journals without
    /// re-attempting the network: the grace period has already been spent, and spending it again per
    /// call would turn a clean shutdown into a long one.</summary>
    public bool OwnerLost { get; private set; }

    public RemoteRunnerState(
        HttpClient http, StateJournal journal, TimeSpan grace, Action<string> report, TimeSpan? retryDelay = null)
    {
        _http = http;
        _journal = journal;
        _grace = grace;
        _report = report;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    // ---- Prerequisites: fail rather than invent ----

    public void UpsertTask(string taskName, bool enabled) =>
        Required("upsert-task", new UpsertTaskRequest(taskName, enabled));

    public bool HasOutstandingWork(string taskName) =>
        Required<BoolResponse>($"has-outstanding-work?taskName={Uri.EscapeDataString(taskName)}").Value;

    public WorkItem? TryClaimNext(string taskName, string workerId) =>
        Required<WorkItemResponse>("try-claim-next", new TryClaimNextRequest(taskName, workerId)).Item;

    public bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        Required<BoolResponse>("try-acquire-lock", new TryAcquireLockRequest(taskName, runKind, mappingName, runId)).Value;

    public string? GetWatermark(string taskName, string sourceTable) =>
        Required<WatermarkResponse>(
            $"watermark?taskName={Uri.EscapeDataString(taskName)}&sourceTable={Uri.EscapeDataString(sourceTable)}").Watermark;

    public void BeginRun(Guid runId, int? pid) => Required("begin-run", new BeginRunRequest(runId, pid));

    // ---- Outcomes: journal rather than lose ----

    public void MarkRunning(long id) => Outcome("mark-running", new WorkItemRequest(id), JournalOperation.MarkRunning);

    public void MarkDone(long id) => Outcome("mark-done", new WorkItemRequest(id), JournalOperation.MarkDone);

    public void MarkFailed(long id) => Outcome("mark-failed", new WorkItemRequest(id), JournalOperation.MarkFailed);

    public void ReleaseClaim(long id) => Outcome("release-claim", new WorkItemRequest(id), JournalOperation.ReleaseClaim);

    public void ReleaseLock(string taskName, RunKind runKind, string mappingName) =>
        Outcome("release-lock", new ReleaseLockRequest(taskName, runKind, mappingName), JournalOperation.ReleaseLock);

    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null) =>
        Outcome("complete-run", new CompleteRunRequest(runId, status, rowsRead, rowsWritten, errorSummary, failureKind),
            JournalOperation.CompleteRun);

    public void SetWatermark(string taskName, string sourceTable, string watermark) =>
        Outcome("set-watermark", new SetWatermarkRequest(taskName, sourceTable, watermark), JournalOperation.SetWatermark);

    public void RecordVerificationResult(VerificationResultRecord result) =>
        Outcome("record-verification-result", new RecordVerificationResultRequest(result),
            JournalOperation.RecordVerificationResult);

    /// <summary>
    /// Buffered, not sent. Log lines are the highest-rate write here — one per line — and one HTTP
    /// round trip each is the difference between hundreds a second and tens of thousands.
    /// </summary>
    public void Log(Guid runId, LogSeverity level, string message)
    {
        lock (_gate)
        {
            _pendingLogs.Add(new LogRequest(runId, level, message, DateTimeOffset.UtcNow));
            if (_pendingLogs.Count < 200)
                return;
        }
        Flush();
    }

    public void Flush()
    {
        LogRequest[] batch;
        lock (_gate)
        {
            if (_pendingLogs.Count == 0)
                return;
            batch = [.. _pendingLogs];
            _pendingLogs.Clear();
        }

        // Journalled per line rather than per batch, so replay is line-ordered and a truncated file
        // loses one entry rather than two hundred.
        if (OwnerLost)
        {
            foreach (var entry in batch)
                _journal.Append(JournalOperation.Log, entry);
            return;
        }

        try
        {
            Send("log-batch", new LogBatchRequest(batch));
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            DeclareOwnerLost(ex);
            foreach (var entry in batch)
                _journal.Append(JournalOperation.Log, entry);
        }
    }

    // ---- Plumbing ----

    private void Outcome(string path, object body, JournalOperation operation)
    {
        if (OwnerLost)
        {
            _journal.Append(operation, body);
            return;
        }

        try
        {
            Send(path, body);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            DeclareOwnerLost(ex);
            _journal.Append(operation, body);
        }
    }

    private void Required(string path, object body) => WithGrace<object?>(() => { Send(path, body); return null; });

    private T Required<T>(string path, object? body = null) => WithGrace(() => Send<T>(path, body));

    /// <summary>
    /// A prerequisite is retried for the grace period before it is given up on, because the owner is
    /// this process's own parent and a restart should be far shorter than that. Once the period is
    /// spent, the runner is told the owner is gone and is expected to stop — not to keep trying, which
    /// is what turns a clean shutdown into a hang.
    /// </summary>
    private T WithGrace<T>(Func<T> action)
    {
        if (OwnerLost)
            throw new StateOwnerUnavailableException("The state owner is unavailable and this run is shutting down.");

        var deadline = DateTimeOffset.UtcNow + _grace;
        Exception? last = null;
        var reported = false;

        while (true)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                last = ex;
                if (!reported)
                {
                    _report($"The state owner is not responding; retrying for up to {_grace.TotalSeconds:F0}s.");
                    reported = true;
                }
                if (DateTimeOffset.UtcNow >= deadline)
                    break;
                Thread.Sleep(_retryDelay);
            }
        }

        DeclareOwnerLost(last!);
        throw new StateOwnerUnavailableException(
            $"The state owner did not respond within {_grace.TotalSeconds:F0}s.", last);
    }

    private void DeclareOwnerLost(Exception cause)
    {
        if (OwnerLost)
            return;
        OwnerLost = true;
        // Deliberately "from here on", not "recorded": a runner that loses its owner while it has
        // nothing outstanding writes no file at all, and a message claiming otherwise sends whoever
        // reads it looking for one.
        _report($"The state owner is unreachable ({cause.GetType().Name}: {cause.Message}). " +
                $"Any outcomes from here on go to {_journal.FilePath}; this run is shutting down.");
    }

    private void Send(string path, object? body) => Send<object?>(path, body);

    private T Send<T>(string path, object? body)
    {
        using var request = new HttpRequestMessage(
            body is null ? HttpMethod.Get : HttpMethod.Post, $"{StateProtocol.Route}/{path}");
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        using var response = _http.Send(request);
        response.EnsureSuccessStatusCode();

        if (typeof(T) == typeof(object))
            return default!;

        using var stream = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<T>(stream, Json)!;
    }

    /// <summary>
    /// "The owner is gone", as distinct from "the owner said no". A 4xx is the owner answering and is a
    /// bug to surface, not a reason to spill — spilling on a rejected request would journal work the
    /// owner had already refused.
    /// </summary>
    private static bool IsUnreachable(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError } => true,
        TaskCanceledException or TimeoutException or IOException => true,
        _ => false,
    };

    public void Dispose()
    {
        try { Flush(); } catch (StateOwnerUnavailableException) { }
        _journal.Dispose();
    }
}
