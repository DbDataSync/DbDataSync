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

    /// <summary>Puts the previous version back: uninstall what was just installed, install what was there. From
    /// <paramref name="rollbackPackageDirectory"/> when the package was kept, otherwise from nuget.org by exact
    /// version — which only a stable or beta version can be.</summary>
    public static IReadOnlyList<ToolInvocation> Rollback(UpdateRequest applied, string? rollbackPackageDirectory)
    {
        var location = new InstallLocation(applied.InstallKind, applied.ToolRoot);
        var install = new List<string>(Location(location)) { ReleaseSources.PackageId };
        if (rollbackPackageDirectory is not null)
            install.AddRange(["--add-source", rollbackPackageDirectory]);
        install.AddRange(["--version", applied.PreviousVersion!]);

        return [Uninstall(location), new ToolInvocation("install", ["tool", "install", .. install])];
    }

    private static ToolInvocation Uninstall(InstallLocation location) =>
        new("remove", ["tool", "uninstall", .. Location(location), ReleaseSources.PackageId]);

    private static string[] Location(InstallLocation location) =>
        location.Kind == InstallKind.Global ? ["--global"] : ["--tool-path", location.ToolRoot!];
}
