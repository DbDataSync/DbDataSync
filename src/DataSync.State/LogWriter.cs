using System.Collections.Concurrent;
using System.Data.Common;

namespace DataSync.State;

/// <summary>
/// Batches log lines and flushes them in one transaction instead of one commit per line — the
/// SQLite write-contention mitigation from architecture/detailed-design.md §3.7. Flushes when the
/// buffer reaches <see cref="FlushThreshold"/> lines, on a periodic timer, or on <see cref="Dispose"/>.
/// One instance is expected per process (API, or a TaskRunner run) and is safe for concurrent
/// <see cref="Log"/> calls from multiple threads within that process.
/// </summary>
public sealed class LogWriter : IDisposable
{
    private const int FlushThreshold = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly StateDatabase _database;
    private readonly ConcurrentQueue<(Guid RunId, DateTimeOffset TimestampUtc, LogSeverity Level, string Message, string? SourceKey)> _buffer = new();
    private readonly PeriodicTimer _timer;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _cts = new();
    private int _pendingCount;

    public LogWriter(StateDatabase database)
    {
        _database = database;
        _timer = new PeriodicTimer(FlushInterval);
        _flushLoop = RunFlushLoopAsync();
    }

    public void Log(Guid runId, LogSeverity level, string message) =>
        Log(runId, level, message, DateTimeOffset.UtcNow, sourceKey: null);

    /// <summary>
    /// Records a line that happened somewhere else and arrived later — a runner's state journal,
    /// replayed after the owner came back.
    /// <para>
    /// It carries its original timestamp, because a recovered run whose every line is stamped with
    /// the moment of recovery is a run whose log says nothing about when anything happened. And it
    /// carries a <paramref name="sourceKey"/>, which makes re-applying the same journal entry a no-op
    /// rather than a second copy of the line.
    /// </para>
    /// </summary>
    public void Log(Guid runId, LogSeverity level, string message, DateTimeOffset timestampUtc, string? sourceKey)
    {
        _buffer.Enqueue((runId, timestampUtc, level, message, sourceKey));
        if (Interlocked.Increment(ref _pendingCount) >= FlushThreshold)
            Flush();
    }

    public void Flush()
    {
        var batch = new List<(Guid RunId, DateTimeOffset TimestampUtc, LogSeverity Level, string Message, string? SourceKey)>();
        while (_buffer.TryDequeue(out var entry))
        {
            batch.Add(entry);
            Interlocked.Decrement(ref _pendingCount);
        }

        if (batch.Count == 0)
            return;

        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            // Insert-or-ignore against UX_Logs_SourceKey: a replayed journal entry is dropped, and a
            // live line (SourceKey NULL) never conflicts. The conflicting column is named explicitly
            // — the untargeted form the other two engines allow has no SQL Server equivalent.
            using var cmd = _database.Command(connection, transaction, _database.Dialect.InsertOrIgnore(
                "Logs",
                "RunId, TimestampUtc, Level, Message, SourceKey",
                "$runId, $ts, $level, $message, $sourceKey",
                "SourceKey",
                // UX_Logs_SourceKey is partial — unique only where SourceKey is not null, which is
                // what lets two genuinely identical live lines both be stored. The predicate has to
                // travel with the target or the index is not the one being matched against.
                "SourceKey IS NOT NULL"));

            // One command, bound once and re-executed per line. A batch is the whole point of this
            // writer, and rebuilding the parameter collection for each of a few hundred lines would
            // undo it.
            var runIdParam = Reusable(cmd, "runId");
            var tsParam = Reusable(cmd, "ts");
            var levelParam = Reusable(cmd, "level");
            var messageParam = Reusable(cmd, "message");
            var sourceKeyParam = Reusable(cmd, "sourceKey");

            foreach (var entry in batch)
            {
                runIdParam.Value = entry.RunId.ToString();
                tsParam.Value = entry.TimestampUtc.ToString("O");
                levelParam.Value = entry.Level.ToString();
                messageParam.Value = entry.Message;
                sourceKeyParam.Value = (object?)entry.SourceKey ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });

        DbParameter Reusable(DbCommand cmd, string name)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = _database.Dialect.ParameterName(name);
            cmd.Parameters.Add(parameter);
            return parameter;
        }
    }

    public IReadOnlyList<LogEntryRecord> GetLogs(Guid runId, long? sinceId = null)
    {
        Flush();

        return _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var cmd = _database.Command(connection, sinceId is null
                ? "SELECT Id, RunId, TimestampUtc, Level, Message FROM Logs WHERE RunId = $runId ORDER BY Id;"
                : "SELECT Id, RunId, TimestampUtc, Level, Message FROM Logs WHERE RunId = $runId AND Id > $sinceId ORDER BY Id;");
            cmd.Bind(_database, "runId", runId.ToString());
            if (sinceId is not null)
                cmd.Bind(_database, "sinceId", sinceId.Value);

            using var reader = cmd.ExecuteReader();
            var results = new List<LogEntryRecord>();
            while (reader.Read())
            {
                results.Add(new LogEntryRecord(
                    reader.GetInt64(0),
                    Guid.Parse(reader.GetString(1)),
                    DateTimeOffset.Parse(reader.GetString(2)),
                    Enum.Parse<LogSeverity>(reader.GetString(3)),
                    reader.GetString(4)));
            }

            return (IReadOnlyList<LogEntryRecord>)results;
        });
    }

    private async Task RunFlushLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                Flush();
        }
        catch (OperationCanceledException)
        {
            // Dispose() requested shutdown.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            _flushLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Best-effort: the loop is expected to end via OperationCanceledException.
        }

        Flush();
        _cts.Dispose();
    }
}
