using DbDataSync.Core.Git;

namespace DbDataSync.Core.Config;

/// <summary>
/// What a restore did — phase 35.
/// </summary>
/// <param name="RestoredFromSha">The commit the config was put back to.</param>
/// <param name="CommitSha">
/// The **new** commit recording the restore, or null when the restore was a no-op because the config
/// was already in that state. History is never rewritten here: restoring to an older commit adds to
/// the log rather than removing from it, so the log reads honestly and the undo is itself undoable.
/// </param>
/// <param name="Changes">Which files appeared, disappeared or differ. The same list the confirmation
/// showed before the operator agreed to it, computed from the same diff.</param>
/// <param name="Warnings">
/// Things that are true about the restored config and are not reasons to refuse it — a connection it
/// names that does not exist, most likely. Refusing on these would make a restore stricter than the
/// save it restores, which is the opposite of the rule the validation exists to hold.
/// </param>
public sealed record ConfigRestoreResult(
    string RestoredFromSha,
    string? CommitSha,
    IReadOnlyList<ConfigFileChange> Changes,
    IReadOnlyList<string> Warnings);
