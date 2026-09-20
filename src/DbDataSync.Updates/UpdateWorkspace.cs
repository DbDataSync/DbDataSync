namespace DbDataSync.Updates;

/// <summary>
/// Where an update keeps its files, split by **who is allowed to write them** — the whole safety of updating from
/// the console rests on that split.
/// <para>
/// **<see cref="Directory"/> is the service's, and is untrusted.** <c>&lt;data root&gt;/updates/</c> is inside the
/// hardened unit's <c>ReadWritePaths</c>, so a compromised service can put anything in it. It holds only what the
/// service legitimately writes — a request naming a version (<see cref="PendingPath"/>), a note that the new
/// version has served long enough (<see cref="ConfirmedPath"/>), and the display state the console shows
/// (<see cref="StatePath"/>). The privileged step that runs as root reads **one string** from it (the version
/// asked for) and derives everything else itself.
/// </para>
/// <para>
/// **<see cref="PrivilegedDirectory"/> is root's, and is trusted.** The record that an update is on trial, the copy
/// of the outgoing package used to roll back, and the log live where the service cannot write. Were they in the
/// service's directory, it could plant a package there and have root install it as a "rollback".
/// </para>
/// <para>
/// For a caller with no such boundary (a test; the CLI, which is the operator and supplies a private temporary
/// directory of its own) the two are simply the same directory.
/// </para>
/// </summary>
public sealed class UpdateWorkspace(string dataRoot, string? privilegedDirectory = null)
{
    /// <summary>Where the systemd unit's privileged step keeps its own files on Linux. Not under the data root:
    /// that is owned by the service's user, who could rename anything in it out from under root.</summary>
    public const string DefaultPrivilegedDirectory = "/var/lib/dbdatasync-update";

    /// <summary>Absolute. Every path here can end up on a <c>dotnet</c> command line, and <c>dotnet</c> is run from a
    /// working directory of its own — a relative path would silently mean something else there.</summary>
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);

    /// <summary>The service-writable directory. Untrusted input as far as a privileged step is concerned.</summary>
    public string Directory => Path.Combine(DataRoot, "updates");

    /// <summary>Root's directory when there is a boundary to keep; otherwise the same as <see cref="Directory"/>.</summary>
    public string PrivilegedDirectory { get; } = Path.GetFullPath(privilegedDirectory ?? Path.Combine(dataRoot, "updates"));

    // --- the service's: untrusted ------------------------------------------------------------------------

    /// <summary>An update has been requested and not yet applied. Holds a version, and who asked.</summary>
    public string PendingPath => Path.Combine(Directory, "pending-update.json");

    /// <summary>The new version says it has served long enough. Read by the privileged step at the next start.</summary>
    public string ConfirmedPath => Path.Combine(Directory, "confirmed-update.json");

    /// <summary>What the console and <c>update --status</c> show: the current phase and the last few outcomes.
    /// **Display only** — nothing that decides what gets installed is ever read from here.</summary>
    public string StatePath => Path.Combine(Directory, "update-state.json");

    // --- root's: trusted ---------------------------------------------------------------------------------

    /// <summary>An update has been applied and its version has not yet been confirmed.</summary>
    public string AppliedPath => Path.Combine(PrivilegedDirectory, "applied-update.json");

    /// <summary>Holds the package of the version being replaced, for as long as it might be needed.</summary>
    public string RollbackDirectory => Path.Combine(PrivilegedDirectory, "rollback");

    public string LogPath => Path.Combine(PrivilegedDirectory, "update.log");

    /// <summary>A home for <c>dotnet</c> to use when there is none — a service's environment often has no
    /// <c>HOME</c>, and <c>dotnet tool</c> wants somewhere for its first-run state and package cache.</summary>
    public string DotnetHome => Path.Combine(PrivilegedDirectory, "dotnet-home");

    /// <summary>Only the CLI's own leftover staging folder, cleaned up when it finishes.</summary>
    public string StagedDirectory => Path.Combine(PrivilegedDirectory, "staged");
}
