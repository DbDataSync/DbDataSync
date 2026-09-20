using System.Net;
using DbDataSync.Updates;

namespace DbDataSync.Cli;

/// <summary>Stops and starts the service an update applies to. Behind an interface so the whole flow can be
/// driven without a real service manager.</summary>
internal interface IServiceControl
{
    /// <returns>0 on success; anything else means it could not be done (usually: not root).</returns>
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

/// <summary>
/// Carries out an update from a terminal, start to finish: stop the service, install, start it, and check it
/// answers — rolling back if it does not.
/// <para>
/// This is the **synchronous** path. The CLI is not the service, so it can stop the service, replace the tool
/// (on Linux a running process does not mind), start it again and judge the result itself; nothing is left "on
/// trial" for the service's own next start to roll back. The console's path is different because the process
/// asking is the service, which cannot outlive its own replacement — see the phase 159 design.
/// </para>
/// </summary>
internal static class UpdateApplyFlow
{
    public static async Task<int> RunAsync(
        UpdateRequest request, ServiceSituation service, UpdateApplier applier, IServiceControl control, IHealthProbe health,
        string healthUrl, TimeSpan healthTimeout, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var hasService = service.Manager != ServiceManager.None;

        if (hasService)
        {
            output.WriteLine("Stopping the service …");
            if (control.Stop(service) != 0)
            {
                error.WriteLine("Could not stop the service, so nothing was changed. Stopping it needs root — run this with sudo.");
                return 1;
            }
        }

        output.WriteLine($"Installing {request.TargetVersion} …");
        var installed = await applier.ApplyNowAsync(request, cancellationToken);
        if (installed.Outcome != ApplyOutcome.Applied)
        {
            error.WriteLine(installed.Message);
            if (hasService)
            {
                output.WriteLine("Starting the service again on the version that was already installed …");
                control.Start(service);
            }

            return 1;
        }

        if (!hasService)
        {
            applier.Complete(request);
            output.WriteLine($"Installed {request.TargetVersion}. Nothing was running as a service — restart `dbdatasync serve` to use it.");
            return 0;
        }

        output.WriteLine("Starting the service …");
        if (control.Start(service) == 0
            && await health.WaitUntilHealthyAsync(healthUrl, healthTimeout, cancellationToken))
        {
            applier.Complete(request);
            output.WriteLine($"Updated to {request.TargetVersion}; the service is answering.");
            return 0;
        }

        output.WriteLine($"The service did not answer at {healthUrl} within {(int)healthTimeout.TotalSeconds} seconds. Rolling back to {request.PreviousVersion} …");
        control.Stop(service);
        var rolledBack = await applier.RollBackAsync(request, "the service did not answer after the update", cancellationToken);
        control.Start(service);

        error.WriteLine(rolledBack.Outcome == ApplyOutcome.RolledBack
            ? $"Rolled back: {request.PreviousVersion} is installed again. Details are in {applier.LogPath}."
            : $"Rolling back failed: {rolledBack.Message} Details are in {applier.LogPath}.");
        return 1;
    }
}
