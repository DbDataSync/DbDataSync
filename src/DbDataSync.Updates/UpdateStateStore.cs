using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataSync.Updates;

/// <summary>
/// Reads and writes the small JSON files that carry an update between the processes involved in it — and keeps
/// the two trust levels of <see cref="UpdateWorkspace"/> apart.
/// <para>
/// **Reads from the service's directory are treated as hostile.** Anything the service can write, a compromised
/// service can write: so a symbolic link, an oversized file or malformed JSON is simply "absent". The privileged
/// step then goes on to use only what it can verify itself.
/// </para>
/// <para>
/// **Every write is a temp file moved over the target**, never an in-place overwrite. That is what lets the
/// service replace a file the privileged step created (rename needs only write access to the directory) — and it
/// means writing over a path that was swapped for a symlink replaces the link, not what it pointed at. Writing
/// into the service's directory refuses if that directory is itself a link.
/// </para>
/// <para>
/// No <c>.gitignore</c> is touched: the data root is not a git repository (the config repo is its <c>config/</c>
/// subdirectory), and appending to a file in a directory the service controls, as root, is not something to do
/// for a convention that means nothing here.
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

    // --- the service's directory: untrusted ---------------------------------------------------------------

    public PendingUpdate? ReadPending() => Read<PendingUpdate>(Workspace.PendingPath);

    public void WritePending(PendingUpdate request) => Write(Workspace.PendingPath, request);

    public void ClearPending() => Delete(Workspace.PendingPath);

    public ConfirmedUpdate? ReadConfirmed() => Read<ConfirmedUpdate>(Workspace.ConfirmedPath);

    public void WriteConfirmed(string targetVersion) =>
        Write(Workspace.ConfirmedPath, new ConfirmedUpdate(targetVersion, DateTimeOffset.UtcNow));

    public void ClearConfirmed() => Delete(Workspace.ConfirmedPath);

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

    // --- root's directory: trusted -------------------------------------------------------------------------

    public UpdateRequest? ReadApplied() => Read<UpdateRequest>(Workspace.AppliedPath);

    public void WriteApplied(UpdateRequest request) => Write(Workspace.AppliedPath, request);

    public void ClearApplied() => Delete(Workspace.AppliedPath);

    // --- plumbing -------------------------------------------------------------------------------------------

    private T? Read<T>(string path) where T : class
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
            // A half-written, hand-edited or unreadable file is treated as absent rather than wedging every
            // start: the caller acts on what it can read, and the next write replaces it.
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        if (IsLinkedDirectory(directory))
            throw new IOException($"'{directory}' is a symbolic link; refusing to write through it.");

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            File.Delete(path);
    }

    /// <summary>The privileged directory is created readable by everyone and writable by its creator — root, on
    /// Linux — so the service and an admin can read the log and the record without being able to change them.</summary>
    private void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
            return;

        if (OperatingSystem.IsWindows() || !string.Equals(directory, Workspace.PrivilegedDirectory, StringComparison.Ordinal))
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static bool IsLinkedDirectory(string directory) => new DirectoryInfo(directory).LinkTarget is not null;

    /// <summary>Display text only, but it is shown in a browser and written to a log: no control characters, and
    /// bounded, whatever a compromised service put in it.</summary>
    internal static string? Clean(string? text)
    {
        if (text is null)
            return null;

        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= 500 ? cleaned : cleaned[..500];
    }
}
