namespace DbDataSync.Updates;

/// <summary>
/// Where an update records what it did: <c>&lt;data root&gt;/updates/</c>, the directory the console reads to show
/// an update in progress and the recent history.
/// <para>
/// **Display only.** Since phase 196L nothing decides what gets installed from here: an update is run by an operator
/// from a shell (<c>dbdatasync update --apply</c>), who says what to install on the command line. The service can
/// write this directory, so the reader (<see cref="UpdateStateStore"/>) still treats what it finds as untrusted text.
/// Phase 159's second, root-owned directory — the record a privileged pre-start step acted on and the package it
/// kept for a rollback — went with that step: a rollback is now a pointer flip, with nothing to keep.
/// </para>
/// </summary>
public sealed class UpdateWorkspace(string dataRoot)
{
    /// <summary>Absolute: the log path is printed for an operator to open, from wherever they are.</summary>
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);

    public string Directory => Path.Combine(DataRoot, "updates");

    /// <summary>What the console and <c>update --status</c> show: the current phase and the last few outcomes.</summary>
    public string StatePath => Path.Combine(Directory, "update-state.json");

    /// <summary>The commands an update ran and what they printed.</summary>
    public string LogPath => Path.Combine(Directory, "update.log");
}
