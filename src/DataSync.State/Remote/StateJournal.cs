using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.State.Remote;

/// <summary>One recorded outcome, as it sits on a line of a journal file.</summary>
/// <param name="Sequence">Per-journal, monotonic. Makes replay ordered and lets a partial replay be
/// detected.</param>
public sealed record JournalEntry(
    long Sequence,
    DateTimeOffset AtUtc,
    JournalOperation Operation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload);

/// <summary>
/// The operations that may be spilled — deliberately only the ones that record work already done.
/// <para>
/// A prerequisite (claiming work, taking a lock, reading a watermark) is absent by design: there is no
/// outcome to preserve, and replaying an invented one would grant a runner something the owner never
/// gave it.
/// </para>
/// </summary>
public enum JournalOperation
{
    Log,
    MarkRunning,
    MarkDone,
    MarkFailed,
    /// <summary>A claimed-but-unfinished item, returned to the queue. Never <see cref="MarkDone"/> —
    /// the work did not finish, and recording that it did would drop it silently.</summary>
    ReleaseClaim,
    ReleaseLock,
    CompleteRun,
    /// <summary>Only ever written after the target write committed, which is what makes replaying it
    /// safe: its presence is the evidence.</summary>
    SetWatermark,
}

/// <summary>
/// An append-only record of outcomes a runner could not deliver, for the owner to apply when it
/// returns.
/// <para>
/// **JSON Lines, not a JSON document.** One entry per line, flushed as it is written, so a file
/// truncated by a kill or a full disk loses only its last line and everything before it still replays.
/// A single JSON object would be unparseable after the same interruption — which is the interruption
/// this exists for.
/// </para>
/// <para>
/// One file per run under <c>&lt;state-dir&gt;/pending/&lt;replication&gt;/&lt;runId&gt;.jsonl</c>, so
/// the owner can drain a replication's journals before scheduling anything new for it.
/// </para>
/// </summary>
public sealed class StateJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private StreamWriter? _writer;
    private long _sequence;

    public StateJournal(string path) => _path = path;

    /// <summary>Named FilePath, not Path: a member called Path shadows <see cref="System.IO.Path"/>
    /// inside this class and turns every static call into a compile error.</summary>
    public string FilePath => _path;

    /// <summary>True once anything has been written — so a clean shutdown that spilled nothing leaves
    /// no file for the owner to find and report as an incident.</summary>
    public bool HasEntries => _sequence > 0;

    public void Append(JournalOperation operation, object? payload)
    {
        // Opened lazily — directory included: the common case is a run that never loses its owner,
        // and that run should leave nothing behind for the owner to find and report as an incident.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _writer ??= new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        var entry = new JournalEntry(
            ++_sequence, DateTimeOffset.UtcNow, operation,
            payload is null ? null : JsonSerializer.Serialize(payload, payload.GetType(), Json));

        _writer.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    /// <summary>
    /// Reads a journal back. A trailing partial line — the kill this format exists to survive — is
    /// skipped rather than throwing, because losing the last entry is the designed-for outcome and
    /// refusing to read the other five hundred is not.
    /// </summary>
    public static IReadOnlyList<JournalEntry> Read(string path, Action<string>? onSkipped = null)
    {
        var entries = new List<JournalEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line, Json) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                onSkipped?.Invoke(line);
            }
        }
        return entries;
    }

    public static T? PayloadOf<T>(JournalEntry entry) where T : class =>
        entry.Payload is null ? null : JsonSerializer.Deserialize<T>(entry.Payload, Json);

    /// <summary>Where a run's journal lives. Grouped by replication because that is the scope the owner
    /// drains before scheduling.</summary>
    public static string PathFor(string stateDbPath, string taskName, Guid runId) =>
        Path.Combine(DirectoryFor(stateDbPath, taskName), $"{runId:N}.jsonl");

    public static string DirectoryFor(string stateDbPath, string taskName) =>
        Path.Combine(RootFor(stateDbPath), Sanitize(taskName));

    public static string RootFor(string stateDbPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(stateDbPath))!, "pending");

    /// <summary>A replication name is already restricted to letters, digits, '-' and '_' by
    /// <c>ConfigValidation.ValidateName</c> because it becomes a directory name; this is belt-and-braces
    /// for a journal written from a name that arrived some other way.</summary>
    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
