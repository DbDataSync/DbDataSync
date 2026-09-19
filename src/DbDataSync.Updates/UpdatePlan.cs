namespace DbDataSync.Updates;

public enum PlanOperation
{
    /// <summary>The target is the version already installed.</summary>
    AlreadyInstalled,

    /// <summary><c>dotnet tool update</c> — the target is newer.</summary>
    Update,

    /// <summary><c>dotnet tool uninstall</c> then <c>install</c> — the target is <b>older</b>, and
    /// <c>dotnet tool update</c> refuses to go down (checked: "The requested version … is lower than existing
    /// version").</summary>
    Reinstall,
}

public enum ServiceManager
{
    None,
    WindowsService,
    Systemd,
}

/// <param name="Name">The Windows service name or the systemd unit name; null when there is none.</param>
public sealed record ServiceSituation(ServiceManager Manager, string? Name)
{
    public static ServiceSituation None { get; } = new(ServiceManager.None, null);
}

/// <summary>
/// Everything that decides what an update *is* — installed version, target, how the tool is installed, where a
/// snapshot was staged, which service to stop and start — as plain data. Phase 158 renders it as commands for
/// the operator to run; a later phase executes the same object, so the two cannot disagree about what an
/// update does.
/// </summary>
/// <param name="Installed">The running version, or null when it could not be read.</param>
/// <param name="SourceDirectory">The staged folder to use as <c>--add-source</c>. Set for a snapshot, null for
/// stable and beta (nuget.org serves those).</param>
/// <param name="NeedsElevation">True when the install location is not the current user's own — the
/// commands then need <c>sudo</c> or an elevated prompt.</param>
public sealed record UpdatePlan(
    ReleaseVersion? Installed,
    ReleaseInfo Target,
    InstallLocation Location,
    PlanOperation Operation,
    string? SourceDirectory,
    ServiceSituation Service,
    bool NeedsElevation)
{
    /// <summary>Whether there is an install to act on. False for a development build or a container.</summary>
    public bool IsUpdatable => Location.Kind is InstallKind.Global or InstallKind.ToolPath;
}

public static class UpdatePlanner
{
    /// <summary>What it takes to get from <paramref name="installed"/> to <paramref name="target"/>. An
    /// unreadable installed version is treated as an update, which is what most such cases are.</summary>
    public static PlanOperation OperationFor(ReleaseVersion? installed, ReleaseVersion target) =>
        installed is null ? PlanOperation.Update
        : target.CompareTo(installed) switch
        {
            0 => PlanOperation.AlreadyInstalled,
            > 0 => PlanOperation.Update,
            _ => PlanOperation.Reinstall,
        };

    /// <summary>Whether a package has to be downloaded before this plan means anything: a snapshot has no
    /// feed to be fetched from, so unless there is nothing to install (already there) or nowhere to install
    /// it (a container image), it is staged first.</summary>
    public static bool NeedsStagedPackage(ReleaseInfo target, InstallLocation location, PlanOperation operation) =>
        target.Channel == ReleaseChannel.Snapshot
        && location.Kind != InstallKind.Container
        && operation != PlanOperation.AlreadyInstalled;

    public static UpdatePlan Build(
        ReleaseVersion? installed,
        ReleaseInfo target,
        InstallLocation location,
        string? stagedDirectory,
        ServiceSituation service,
        bool needsElevation)
    {
        var operation = OperationFor(installed, target.Version);

        var plan = new UpdatePlan(
            installed, target, location, operation,
            target.Channel == ReleaseChannel.Snapshot ? stagedDirectory : null,
            service, needsElevation);

        if (plan.IsUpdatable && NeedsStagedPackage(target, location, operation) && stagedDirectory is null)
            throw new ArgumentException("A snapshot has to be staged before it can be planned.", nameof(stagedDirectory));

        return plan;
    }
}
