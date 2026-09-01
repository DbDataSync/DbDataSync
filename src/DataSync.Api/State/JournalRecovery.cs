using DataSync.Api.Configuration;
using DataSync.State;
using DataSync.State.Remote;

namespace DataSync.Api.State;

/// <summary>
/// Applies the outcomes a TaskRunner recorded to disk because it could not reach this process, before
/// any new work starts for that replication.
/// <para>
/// This is the unusual path, and it says so: every recovery logs at warning level, because a normal
/// installation never produces a journal and one appearing is worth noticing rather than absorbing.
/// </para>
/// </summary>
public sealed class JournalRecovery(
    LocalRunnerState state, TaskRunStore taskRuns, LogWriter logs, ApiOptions options,
    ILogger<JournalRecovery> logger)
{
    /// <summary>
    /// Drains every journal for one replication. Called before the scheduler starts anything for it, so
    /// recovery never races the work it is recovering from.
    /// </summary>
    public void Recover(string taskName)
    {
        var directory = StateJournal.DirectoryFor(options.StateDbPath, taskName);
        if (!Directory.Exists(directory))
            return;

        // Oldest first: journals from different mappings are independent, but the work queue is not, and
        // last-writer-wins on a work item should be the last thing that actually happened.
        foreach (var path in Directory.GetFiles(directory, "*.jsonl").OrderBy(File.GetLastWriteTimeUtc))
            RecoverOne(taskName, path);

        if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
            Directory.Delete(directory);
    }

    private void RecoverOne(string taskName, string path)
    {
        var runId = Path.GetFileNameWithoutExtension(path);
        List<JournalEntry> entries;
        try
        {
            entries = [.. StateJournal.Read(path, line => logger.LogWarning(
                "Skipped an unreadable line in state journal {Journal} (a truncated final entry is expected " +
                "if the runner was killed mid-write): {Line}", path, Truncate(line)))];
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Could not read state journal {Journal}; leaving it in place.", path);
            return;
        }

        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }

        logger.LogWarning(
            "Applying {Count} state change(s) recovered from disk for replication '{Task}' run {RunId}. " +
            "This process was unavailable while that run was working — see {Journal}.",
            entries.Count, taskName, runId, path);

        foreach (var entry in entries.OrderBy(e => e.Sequence))
            Apply(entry, taskName, runId);

        // Once, not per line: a journal is replayed whole, and a transaction per recovered log line
        // makes recovering a long-running run's output take minutes.
        logs.Flush();

        // Only after everything applied. A crash part-way through replays the whole file, which is why
        // every operation below has to be idempotent.
        File.Delete(path);
    }

    private void Apply(JournalEntry entry, string taskName, string runId)
    {
        switch (entry.Operation)
        {
            case JournalOperation.Log when StateJournal.PayloadOf<LogRequest>(entry) is { } log:
                // Its original timestamp, not now — a recovered run whose every line is stamped with
                // the moment of recovery says nothing about when anything happened. And keyed by
                // journal and sequence, so replaying the file does not duplicate the line.
                logs.Log(log.RunId, log.Level, log.Message, log.TimestampUtc, $"{runId}:{entry.Sequence}");
                break;

            case JournalOperation.MarkRunning when StateJournal.PayloadOf<WorkItemRequest>(entry) is { } w:
                state.MarkRunning(w.WorkItemId);
                break;

            case JournalOperation.MarkDone when StateJournal.PayloadOf<WorkItemRequest>(entry) is { } w:
                state.MarkDone(w.WorkItemId);
                break;

            case JournalOperation.MarkFailed when StateJournal.PayloadOf<WorkItemRequest>(entry) is { } w:
                state.MarkFailed(w.WorkItemId);
                break;

            case JournalOperation.ReleaseClaim when StateJournal.PayloadOf<WorkItemRequest>(entry) is { } w:
                state.ReleaseClaim(w.WorkItemId);
                break;

            case JournalOperation.ReleaseLock when StateJournal.PayloadOf<ReleaseLockRequest>(entry) is { } r:
                state.ReleaseLock(r.TaskName, r.RunKind, r.MappingName);
                break;

            case JournalOperation.CompleteRun when StateJournal.PayloadOf<CompleteRunRequest>(entry) is { } c:
                ApplyRunOutcome(c, taskName, runId);
                break;

            case JournalOperation.RecordVerificationResult
                when StateJournal.PayloadOf<RecordVerificationResultRequest>(entry) is { } v:
                // The parquet is already on disk and the index is keyed on (run, check), so replaying
                // this writes the same row rather than a second one.
                state.RecordVerificationResult(v.Result);
                break;

            case JournalOperation.SetWatermark when StateJournal.PayloadOf<SetWatermarkRequest>(entry) is { } s:
                // Safe to replay by construction: a watermark only reaches a journal after the target
                // write committed, so its presence here is the evidence that it did.
                //
                // Except when the entry predates phase 74 and names no mapping. There is nothing to
                // recover the mapping from — that ambiguity is the whole reason the key changed — and
                // replaying it under an invented one would hand some mapping a position that is not
                // its own. Skipped instead, which costs the same re-read the migration already cost.
                if (s.MappingName is null)
                {
                    logger.LogWarning(
                        "Skipping journalled watermark for run {RunId}: it names no mapping, so it was written " +
                        "before the watermark key included one. The mapping re-reads from its stored position.",
                        runId);
                    break;
                }

                state.SetWatermark(s.TaskName, s.MappingName, s.SourceTable, s.Watermark);
                break;

            default:
                logger.LogWarning(
                    "Ignoring unrecognised state journal entry {Sequence} ({Operation}) for run {RunId}.",
                    entry.Sequence, entry.Operation, runId);
                break;
        }
    }

    /// <summary>
    /// The one place recovery can contradict what this process already believes.
    /// <para>
    /// Having watched its child exit, the API may have marked the run **failed**; the journal may say it
    /// **succeeded**. Both observations are honest and they disagree — and the journal wins, on
    /// asymmetric knowledge: this process watched a process exit, while the runner watched whether the
    /// rows actually landed.
    /// </para>
    /// <para>
    /// The correction is logged rather than applied quietly, because an outcome changing after the fact
    /// is exactly the thing an operator should be able to find later.
    /// </para>
    /// </summary>
    private void ApplyRunOutcome(CompleteRunRequest request, string taskName, string runId)
    {
        var existing = taskRuns.GetRun(request.RunId);
        if (existing is not null && existing.Status != request.Status
            && existing.Status is RunStatus.Failed or RunStatus.Cancelled)
        {
            logger.LogWarning(
                "Correcting run {RunId} of '{Task}' from {Was} to {Now} from its state journal: this process " +
                "recorded the outcome from the runner's exit, the runner recorded it from the work itself.",
                runId, taskName, existing.Status, request.Status);
        }

        state.CompleteRun(
            request.RunId, request.Status, request.RowsRead, request.RowsWritten, request.ErrorSummary,
            request.FailureKind, request.Timing, request.PreviousWatermark, request.NewWatermark);
    }

    private static string Truncate(string line) => line.Length <= 200 ? line : line[..200] + "…";
}
