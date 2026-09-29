namespace DbDataSync.Updates;

/// <param name="Purpose">What the invocation is for — <c>remove</c>, <c>install</c> or <c>update</c> — so a
/// renderer can title it without parsing arguments.</param>
/// <param name="Arguments">Everything after <c>dotnet</c>.</param>
public sealed record ToolInvocation(string Purpose, IReadOnlyList<string> Arguments);

/// <summary>
/// The <c>dotnet tool</c> invocations that carry an <see cref="UpdatePlan"/> out. **The one place they are
/// written down**: phase 158 prints them for the operator to run, phase 159 runs the same ones itself, and
/// because both come from here the printed plan and the executed one cannot disagree.
/// </summary>
public static class UpdateCommands
{
    /// <summary>Installs the plan's target: an update if it is newer, an uninstall and an install if it is
    /// older (<c>dotnet tool update</c> refuses to go down), nothing if it is already there.</summary>
    public static IReadOnlyList<ToolInvocation> Install(UpdatePlan plan)
    {
        if (plan.Operation == PlanOperation.AlreadyInstalled)
            return [];

        var location = Location(plan.Location);
        var install = new List<string>(location) { ReleaseSources.PackageId };
        if (plan.SourceDirectory is not null)
            install.AddRange(["--add-source", plan.SourceDirectory]);
        install.AddRange(["--version", plan.Target.Version.Text]);

        return plan.Operation == PlanOperation.Reinstall
            ? [Uninstall(plan.Location), new ToolInvocation("install", ["tool", "install", .. install])]
            : [new ToolInvocation("update", ["tool", "update", .. install])];
    }

    /// <summary>Whether a slot holding <paramref name="slotVersion"/> already holds <paramref name="version"/>.</summary>
    public static bool SlotHolds(string? slotVersion, string version) =>
        slotVersion is not null
        && ReleaseVersion.TryParse(slotVersion, out var there) && ReleaseVersion.TryParse(version, out var wanted) && there.Equals(wanted);

    /// <summary>
    /// Phase 196L: installs <paramref name="version"/> into an **empty** slot. The caller empties it first (deletes
    /// the directory) rather than choosing <c>update</c> or uninstall + install by direction: the slot holds whatever
    /// was current two updates ago, newer or older than the target or half-installed by an update that was
    /// interrupted, and nothing runs from it — so starting clean is always right, and one command covers every case.
    /// </summary>
    /// <param name="sourceDirectory">A staged snapshot's folder, or a folder holding a package already on disk.</param>
    public static ToolInvocation IntoEmptySlot(string slotDirectory, string version, string? sourceDirectory)
    {
        var install = new List<string>(Location(new InstallLocation(InstallKind.ToolPath, slotDirectory))) { ReleaseSources.PackageId };
        if (sourceDirectory is not null)
            install.AddRange(["--add-source", sourceDirectory]);
        install.AddRange(["--version", version]);
        return new ToolInvocation("install", ["tool", "install", .. install]);
    }

    private static ToolInvocation Uninstall(InstallLocation location) =>
        new("remove", ["tool", "uninstall", .. Location(location), ReleaseSources.PackageId]);

    private static string[] Location(InstallLocation location) =>
        location.Kind == InstallKind.Global ? ["--global"] : ["--tool-path", location.ToolRoot!];
}
