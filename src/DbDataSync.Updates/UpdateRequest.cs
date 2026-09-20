namespace DbDataSync.Updates;

/// <summary>
/// What the service writes when an admin asks for an update: **a version, and who asked. Nothing else.**
/// <para>
/// This is the only thing the privileged step reads from the service-writable directory, and it treats it as
/// untrusted: it looks the version up in the pinned release sources itself, finds its own installation itself,
/// downloads any package itself. A request cannot name a source, a folder, a package or a path, because there is
/// nowhere in it to put one.
/// </para>
/// </summary>
public sealed record PendingUpdate(string TargetVersion, DateTimeOffset RequestedUtc, string? RequestedBy);

/// <summary>The new version saying it has been serving for long enough. Untrusted like everything the service
/// writes: forging it can at worst stop a bad version being rolled back.</summary>
public sealed record ConfirmedUpdate(string TargetVersion, DateTimeOffset AtUtc);

/// <summary>
/// An update, as the files under <see cref="UpdateWorkspace"/> record it — enough to rebuild the
/// <see cref="UpdatePlan"/> in a different process, after a restart, without carrying anything else.
/// Strings and enums only, so it round-trips through JSON as itself.
/// </summary>
/// <param name="PreviousVersion">What was installed when the update was requested; null if unknown.</param>
/// <param name="SourceDirectory">The staged folder for a snapshot; null for stable and beta.</param>
/// <param name="RequestedBy">Who asked, for the audit trail: a user name from the console, or the operator's
/// account for the CLI. Display text only.</param>
public sealed record UpdateRequest(
    string TargetVersion,
    string? PreviousVersion,
    ReleaseChannel TargetChannel,
    InstallKind InstallKind,
    string ToolRoot,
    string? SourceDirectory,
    DateTimeOffset RequestedUtc,
    string? RequestedBy)
{
    /// <summary>The same plan phase 158 prints, rebuilt — service handling is not part of applying, so it is
    /// left out.</summary>
    public UpdatePlan ToPlan() => UpdatePlanner.Build(
        PreviousVersion is not null && ReleaseVersion.TryParse(PreviousVersion, out var previous) ? previous : null,
        new ReleaseInfo(ReleaseVersion.Parse(TargetVersion), TargetChannel),
        new InstallLocation(InstallKind, ToolRoot),
        SourceDirectory,
        ServiceSituation.None,
        needsElevation: false);
}

public enum UpdatePhase
{
    Idle,
    Staging,
    Draining,
    Applying,

    /// <summary>Applied; waiting for the new version to come up and prove itself.</summary>
    Restarting,

    Succeeded,
    RolledBack,
    Failed,
}

/// <param name="Message">One sentence a person can read — a failure names what failed.</param>
public sealed record UpdateProgress(
    UpdatePhase Phase,
    string? Message,
    string? FromVersion,
    string? ToVersion,
    DateTimeOffset AtUtc,
    string? RequestedBy)
{
    public bool IsTerminal => Phase is UpdatePhase.Succeeded or UpdatePhase.RolledBack or UpdatePhase.Failed;
}

/// <param name="Current">The latest thing that happened; null before any update was ever attempted.</param>
/// <param name="History">The last few finished updates, newest first.</param>
public sealed record UpdateStateFile(UpdateProgress? Current, IReadOnlyList<UpdateProgress> History);
