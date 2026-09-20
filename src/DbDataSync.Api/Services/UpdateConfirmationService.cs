using DbDataSync.Api.Configuration;
using DbDataSync.Updates;

namespace DbDataSync.Api.Services;

/// <summary>
/// Runs in every start, and does its one real job in the start that follows an applied update: once this
/// version has been serving for <see cref="ApiOptions.SelfUpdateConfirmAfter"/>, it tells the update state so.
/// <para>
/// That is what stops the next start's apply step from rolling this version back. If this version never gets
/// that far — it crashes, or dies ten seconds in — nothing confirms it, and the restart systemd does anyway
/// finds an update that was applied and never proved itself, and undoes it. Confirming at "ready" would not do:
/// a version that starts and then falls over must still be rolled back.
/// </para>
/// </summary>
public sealed class UpdateConfirmationService(
    UpdateService updates,
    ApiOptions options,
    UpdateHostFacts facts,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateConfirmationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        updates.ReconcileOnStartup();

        // On trial: the privileged step applied an update and recorded it as "restarting" for the version this
        // process is running. (Its own record of the update is in a directory this service cannot read.)
        var trial = updates.Store.ReadState().Current;
        if (trial is not { Phase: UpdatePhase.Restarting, ToVersion: { } target })
            return;

        try
        {
            await WaitForStartedAsync(stoppingToken);
            await Task.Delay(options.SelfUpdateConfirmAfter, stoppingToken);

            if (facts.RunningVersion is null)
                return;

            var applier = new UpdateApplier(updates.Store, new ProcessToolCommandRunner(updates.Store.Workspace));
            if (await applier.ConfirmAsync(facts.RunningVersion, stoppingToken))
                logger.LogInformation("Update to {Version} confirmed.", target);
        }
        catch (OperationCanceledException)
        {
            // Stopping before the grace period ended: unconfirmed, so the next start decides — which is right.
        }
    }

    private Task WaitForStartedAsync(CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return Task.CompletedTask;

        var started = new TaskCompletionSource();
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        cancellationToken.Register(() => started.TrySetCanceled(cancellationToken));
        return started.Task;
    }
}
