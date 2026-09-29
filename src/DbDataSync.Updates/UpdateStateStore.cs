using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataSync.Updates;

/// <summary>
/// Reads and writes <see cref="UpdateWorkspace.StatePath"/> — what an update is doing and how the last few ended —
/// and appends to its log.
/// <para>
/// **Reads are treated as hostile.** The service can write this directory, so a symbolic link, an oversized file or
/// malformed JSON is simply "absent", and text read back is cleaned before it is shown (<see cref="Clean"/>).
/// </para>
/// <para>
/// **Every write is a temp file moved over the target**, never an in-place overwrite, so writing over a path that was
/// swapped for a symlink replaces the link, not what it pointed at; writing into a directory that is itself a link is
/// refused.
/// </para>
/// </summary>
public sealed class UpdateStateStore(UpdateWorkspace workspace)
{
    private const int HistoryLimit = 10;

    /// <summary>Far larger than any file this store writes; a bigger one is not one of ours.</summary>
    private const long MaxFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public UpdateWorkspace Workspace { get; } = workspace;

    public UpdateStateFile ReadState() =>
        Read<UpdateStateFile>(Workspace.StatePath) ?? new UpdateStateFile(null, []);

    /// <summary>Records what is happening now. A finished outcome (succeeded, rolled back, failed) also goes
    /// into the history, newest first, capped. Display only.</summary>
    public UpdateProgress Record(UpdatePhase phase, string? message, string? from, string? to, string? requestedBy)
    {
        var progress = new UpdateProgress(phase, Clean(message), from, to, DateTimeOffset.UtcNow, Clean(requestedBy));
        var existing = ReadState();
        var history = progress.IsTerminal
            ? new[] { progress }.Concat(existing.History).Take(HistoryLimit).ToList()
            : existing.History.ToList();
        Write(Workspace.StatePath, new UpdateStateFile(progress, history));
        return progress;
    }

    /// <summary>Appends a timestamped line to the log. Never throws: a log that cannot be written must not stop an
    /// update half way.</summary>
    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Workspace.Directory);
            File.AppendAllText(Workspace.LogPath, $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaxFileBytes || IsLinkedDirectory(Path.GetDirectoryName(path)!))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A half-written, hand-edited or unreadable file is treated as absent: the next write replaces it.
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (IsLinkedDirectory(directory))
            throw new IOException($"'{directory}' is a symbolic link; refusing to write through it.");

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsLinkedDirectory(string directory) => new DirectoryInfo(directory).LinkTarget is not null;

    /// <summary>Display text only, but it is shown in a browser and written to a log: no control characters, and
    /// bounded, whatever was put in the file.</summary>
    internal static string? Clean(string? text)
    {
        if (text is null)
            return null;

        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 500 ? cleaned : cleaned[..500];
    }
}
