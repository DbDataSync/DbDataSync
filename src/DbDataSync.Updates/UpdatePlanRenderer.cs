using System.Text;

namespace DbDataSync.Updates;

/// <summary>
/// Renders an <see cref="UpdatePlan"/> as the commands an operator runs to carry it out. Pure text — the
/// caller decides where it goes — with <c>\n</c> line endings on every platform so it reads the same anywhere
/// and can be compared exactly in a test.
/// <para>
/// Every command is one line, however long: a line continuation is <c>\</c> in one shell, a backtick in
/// another and <c>^</c> in a third, and a plan that can only be pasted into the right shell is not one that
/// can be pasted.
/// </para>
/// </summary>
public static class UpdatePlanRenderer
{
    private const string ToolCommand = "dbdatasync";

    public static string Render(UpdatePlan plan, bool isWindows)
    {
        var text = new StringBuilder();
        RenderHeader(plan, text);
        text.Append('\n');

        switch (plan)
        {
            case { Operation: PlanOperation.AlreadyInstalled }:
                text.Append($"{plan.Target.Version} is already installed — nothing to do.\n");
                break;

            case { Location.Kind: InstallKind.Container }:
                text.Append("This is a container image, not a dotnet tool install. Update it by pulling a newer image tag and recreating the container.\n");
                break;

            case { IsUpdatable: false }:
                text.Append(
                    "This copy of dbdatasync was not installed as a dotnet tool (a development build, `dotnet run`, or unpacked binaries), " +
                    "so there is no install here to update.\n");
                if (plan.SourceDirectory is not null)
                    text.Append($"The staged snapshot is in {plan.SourceDirectory} if you want to install it into a tool location yourself.\n");
                break;

            default:
                RenderSteps(plan, isWindows, text);
                break;
        }

        return text.ToString();
    }

    /// <summary>Just the "Installed / Selected / Staged" lines, for a caller that goes on to carry the plan out
    /// itself rather than print the commands.</summary>
    public static string RenderHeader(UpdatePlan plan)
    {
        var text = new StringBuilder();
        RenderHeader(plan, text);
        return text.ToString();
    }

    private static void RenderHeader(UpdatePlan plan, StringBuilder text)
    {
        text.Append($"Installed   {plan.Installed?.ToString() ?? "(unknown)"}\n");
        text.Append($"Selected    {plan.Target.Version}  ({plan.Target.Channel.ToString().ToLowerInvariant()})\n");
        if (plan.SourceDirectory is not null)
            text.Append($"Staged      {plan.SourceDirectory}  (checksum verified)\n");
    }

    private static void RenderSteps(UpdatePlan plan, bool isWindows, StringBuilder text)
    {
        var elevated = isWindows && plan.NeedsElevation ? " (from an elevated prompt)" : "";
        var sudo = !isWindows && plan.NeedsElevation ? "sudo " : "";
        var steps = new List<(string Title, string[] Commands)>();

        switch (plan.Service)
        {
            case { Manager: ServiceManager.WindowsService, Name: { } name }:
                steps.Add(("Stop the service (from an elevated prompt)", [$"sc.exe stop {name}"]));
                break;
            case { Manager: ServiceManager.Systemd, Name: { } name }:
                steps.Add(("Stop the service", [$"sudo systemctl stop {name}"]));
                break;
            default:
                steps.Add(("Stop any running `dbdatasync serve` — files that are in use cannot be replaced", []));
                break;
        }

        // The commands themselves come from UpdateCommands — the same ones phase 159 runs — so what is
        // printed here and what is executed cannot disagree; only the wording around them lives here.
        foreach (var invocation in UpdateCommands.Install(plan))
        {
            var title = invocation.Purpose switch
            {
                "remove" => $"Remove the installed version — `dotnet tool update` will not go down to an older one{elevated}",
                "install" => $"Install{elevated}",
                _ => $"Update{elevated}",
            };
            steps.Add((title, [$"{sudo}dotnet {string.Join(' ', invocation.Arguments.Select(Quote))}"]));
        }

        switch (plan.Service)
        {
            case { Manager: ServiceManager.WindowsService, Name: { } name }:
                steps.Add(("Start the service (from an elevated prompt)", [$"sc.exe start {name}"]));
                break;
            case { Manager: ServiceManager.Systemd, Name: { } name }:
                steps.Add(("Start the service", [$"sudo systemctl start {name}"]));
                break;
        }

        steps.Add(("Check", [$"{ToolCommand} version"]));

        text.Append("Nothing has been changed. To install it:\n\n");
        for (var i = 0; i < steps.Count; i++)
        {
            text.Append($"  {i + 1}. {steps[i].Title}\n");
            foreach (var command in steps[i].Commands)
                text.Append($"       {command}\n");
        }
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '&', '(', ')', '$', ';', '|', '<', '>', '\'']) >= 0 ? $"\"{value}\"" : value;
}
