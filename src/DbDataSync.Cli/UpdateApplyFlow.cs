using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using System.ServiceProcess;
using DbDataSync.Updates;

namespace DbDataSync.Cli;

/// <summary>Stops and starts the service an update applies to. Behind an interface so the whole flow can be
/// driven without a real service manager.</summary>
internal interface IServiceControl
{
    /// <returns>0 on success; anything else means it could not be done (usually: not root, or not elevated).</returns>
    int Stop(ServiceSituation service);

    int Start(ServiceSituation service);
}

internal interface IHealthProbe
{
    /// <summary>Polls until the service answers, or the timeout passes.</summary>
    Task<bool> WaitUntilHealthyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class SystemctlServiceControl(ISystemdEnvironment environment) : IServiceControl
{
    public int Stop(ServiceSituation service) => Run("stop", service);

    public int Start(ServiceSituation service) => Run("start", service);

    private int Run(string action, ServiceSituation service) =>
        service is { Manager: ServiceManager.Systemd, Name: { } name } ? environment.RunSystemctl(action, name) : 0;
}

/// <summary>
/// Phase 196L: the Windows service, stopped and started through the Service Control Manager and **waited on** —
/// <c>sc.exe stop</c> returns as soon as the stop is requested, and flipping slots while the old version is still
/// shutting down would be exactly the in-use-file problem this design exists to avoid.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceControl : IServiceControl
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    public int Stop(ServiceSituation service) => Run(service, controller =>
    {
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped)
            return;
        if (controller.Status != ServiceControllerStatus.StopPending)
            controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
    });

    public int Start(ServiceSituation service) => Run(service, controller =>
    {
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Running)
            return;
        if (controller.Status != ServiceControllerStatus.StartPending)
            controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, Wait);
    });

    private static int Run(ServiceSituation service, Action<ServiceController> action)
    {
        if (service is not { Manager: ServiceManager.WindowsService, Name: { } name })
            return 0;

        try
        {
            using var controller = new ServiceController(name);
            action(controller);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine($"  {ex.Message}");
            return 1;
        }
    }
}

/// <summary>The same question <c>dbdatasync health</c> asks, asked repeatedly.</summary>
internal sealed class HttpHealthProbe : IHealthProbe
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public async Task<bool> WaitUntilHealthyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var endpoint = $"{baseUrl.TrimEnd('/')}/api/health";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(endpoint, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !deadline.IsCancellationRequested)
                {
                    // Not up yet.
                }

                await Task.Delay(Interval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>A move from one slot to the other: an update (<see cref="Install"/>: put <see cref="ToVersion"/> into
/// <see cref="To"/> first) or a rollback (it is already there).</summary>
/// <param name="FromVersion">What is running now; null when it could not be read.</param>
/// <param name="SourceDirectory">A staged snapshot's folder; null for stable, beta, and a rollback.</param>
internal sealed record SlotSwitch(
    SlotLayout Layout, string From, string To, string? FromVersion, string ToVersion,
    string? SourceDirectory, bool Install, string RequestedBy);

/// <summary>
/// Carries out an update or a rollback from a terminal, start to finish (phase 196L): install into the slot that is
/// **not** running, stop the service, flip the pointer, start it, and check it answers — flipping back if it does
/// not. The same path in both directions; a rollback just skips the install.
/// <para>
/// What makes this safe on Windows is what it never does: it never writes a file the running service or this process
/// is using. The install goes into the other slot, and the only write in between stop and start is the one-line
/// pointer. The old slot is left exactly as it was, so going back is the same flip, with nothing to reinstall.
/// </para>
/// </summary>
internal static class UpdateApplyFlow
{
    public static async Task<int> RunAsync(
        SlotSwitch change, ServiceSituation service, UpdateStateStore store, IToolCommandRunner runner, IServiceControl control,
        IHealthProbe health, string healthUrl, TimeSpan healthTimeout, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var from = change.FromVersion ?? "the current version";
        var noun = change.Install ? "update" : "rollback";

        if (change.Install)
        {
            store.Record(UpdatePhase.Applying, $"Installing {change.ToVersion} into slot {change.To}.", change.FromVersion, change.ToVersion, change.RequestedBy);
            if (!UpdateCommands.SlotHolds(change.Layout.Slot(change.To).Version, change.ToVersion))
                output.WriteLine($"Installing {change.ToVersion} into slot {change.To} ({SlotPaths.SlotDirectory(change.Layout.Root, change.To)}) …");
            var installed = await new SlotInstaller(runner, store).InstallAsync(
                change.Layout, change.To, change.ToVersion, change.SourceDirectory, cancellationToken);
            if (!installed.Succeeded)
            {
                store.Record(UpdatePhase.Failed, installed.Message, change.FromVersion, change.ToVersion, change.RequestedBy);
                error.WriteLine(installed.Message);
                return 1;
            }

            output.WriteLine(installed.Message);
        }

        var hasService = service.Manager != ServiceManager.None;
        if (hasService)
        {
            output.WriteLine("Stopping the service …");
            if (control.Stop(service) != 0)
            {
                var message = "Could not stop the service, so nothing was switched. " + (service.Manager == ServiceManager.WindowsService
                    ? "Stopping it needs an elevated prompt (Run as administrator)."
                    : "Stopping it needs root — run this with sudo.");
                store.Record(UpdatePhase.Failed, message, change.FromVersion, change.ToVersion, change.RequestedBy);
                error.WriteLine(message);
                return 1;
            }
        }

        change.Layout.Flip(change.To);
        store.Log($"switched {change.From} -> {change.To} ({change.FromVersion ?? "?"} -> {change.ToVersion})");

        if (!hasService)
        {
            store.Record(UpdatePhase.Succeeded, $"Switched to {change.ToVersion}.", change.FromVersion, change.ToVersion, change.RequestedBy);
            output.WriteLine(
                $"Switched to {change.ToVersion} (slot {change.To}). The next `dbdatasync` run uses it; nothing runs as a service here, " +
                "so restart a running `dbdatasync serve` yourself.");
            return 0;
        }

        store.Record(UpdatePhase.Restarting, $"Switched to slot {change.To}; starting {change.ToVersion}.", change.FromVersion, change.ToVersion, change.RequestedBy);
        output.WriteLine($"Switched to slot {change.To}. Starting the service …");
        if (control.Start(service) == 0
            && await health.WaitUntilHealthyAsync(healthUrl, healthTimeout, cancellationToken))
        {
            var done = change.Install ? $"Updated to {change.ToVersion}." : $"Rolled back to {change.ToVersion}.";
            store.Record(UpdatePhase.Succeeded, done, change.FromVersion, change.ToVersion, change.RequestedBy);
            output.WriteLine($"{done} The service is answering. {from} is still in slot {change.From}: `dbdatasync update --rollback` switches back to it.");
            return 0;
        }

        output.WriteLine(
            $"The service did not answer at {healthUrl} within {(int)healthTimeout.TotalSeconds} seconds. Switching back to {from} (slot {change.From}) …");
        control.Stop(service);
        change.Layout.Flip(change.From);
        var restarted = control.Start(service) == 0;
        store.Log($"switched back {change.To} -> {change.From}");

        var outcome = $"{change.ToVersion} did not answer after the {noun}, so {from} was switched back in.";
        store.Record(UpdatePhase.RolledBack, outcome, change.FromVersion, change.ToVersion, change.RequestedBy);
        error.WriteLine(restarted
            ? $"Switched back: {from} is running again. {change.ToVersion} stays in slot {change.To}. Details are in {store.Workspace.LogPath}."
            : $"Switched back to {from}, but the service did not start again — start it by hand. Details are in {store.Workspace.LogPath}.");
        return 1;
    }
}
