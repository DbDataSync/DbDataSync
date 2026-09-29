namespace DbDataSync.Updates;

public enum UpdatePhase
{
    Idle,

    /// <summary>Phase 159 only. Kept so a state file written before 196L still reads.</summary>
    Staging,

    /// <summary>Phase 159 only (the console's wind-down). Kept so a state file written before 196L still reads.</summary>
    Draining,

    /// <summary>Installing into the inactive slot.</summary>
    Applying,

    /// <summary>Switched slots; the service is starting and being checked.</summary>
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
