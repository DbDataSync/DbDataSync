using DbDataSync.Api.Configuration;
using DbDataSync.State;
using DbDataSync.Updates;

namespace DbDataSync.Api.Services;

/// <summary>How much work is in flight — runs being executed and worker processes still alive.</summary>
public interface IUpdateWorkProbe
{
    int RunningWorkCount();
}

internal sealed class SupervisorWorkProbe(TaskRunStore taskRuns, ProcessSupervisor supervisor) : IUpdateWorkProbe
{
    public int RunningWorkCount() =>
        taskRuns.GetRunningRuns().Count
        + supervisor.ActiveTaskNames.Count(name => supervisor.DescribeStatus(name).Running);
}

/// <summary>Asks the host to stop so that an update can be applied. An interface because the real thing ends
/// the process, which is not something a test host can survive.</summary>
public interface IUpdateRestart
{
    void StopForUpdate();
}

internal sealed class HostUpdateRestart(IHostApplicationLifetime lifetime) : IUpdateRestart
{
    public void StopForUpdate() => lifetime.StopApplication();
}

public sealed record UpdateCapability(bool Enabled, bool CanApply, string? Reason);

public sealed record UpdateHistoryEntry(string Phase, string? Message, string? FromVersion, string? ToVersion, DateTimeOffset AtUtc, string? RequestedBy);

public sealed record UpdateStatusResponse(
    bool Enabled,
    string? RunningVersion,
    string InstallKind,
    IReadOnlyList<string> Channels,
    bool CanApply,
    string? CannotApplyReason,
    string Phase,
    string? Message,
    DateTimeOffset? AtUtc,
    string? FromVersion,
    string? ToVersion,
    string? RequestedBy,
    bool Pending,
    bool OnTrial,
    string LogPath,
    IReadOnlyList<UpdateHistoryEntry> History);

/// <param name="Url">A page a human can read about this exact version — nuget.org's package-version page
/// for stable/beta, the GitHub Release page for a snapshot. Never null: every channel has one.</param>
public sealed record ReleaseResponse(
    string Version, string Channel, DateTimeOffset? BuiltUtc, bool Installed, bool Newer, string Url);

public enum UpdateRequestOutcome
{
    Accepted,
    Disabled,
    CannotApply,
    ChannelNotEnabled,
    InvalidVersion,
    NotFound,
    AlreadyInstalled,
    InProgress,
    SourceUnavailable,
}

public sealed record UpdateRequestResult(UpdateRequestOutcome Outcome, string Message);

/// <summary>
/// Updating this installation from the web console (phase 159).
/// <para>
/// **The service cannot apply its own update** — it cannot replace the code it is running, and under the
/// hardened systemd unit it cannot even write the tool directory. So it does the part only it can: check the
/// request, write down **the version** asked for, wind work down, and exit with a reserved code. The unit's
/// privileged <c>ExecStartPre=+</c> step (<c>dbdatasync internal apply-update</c>) then applies it before the
/// next start, and <see cref="UpdateConfirmationService"/> in the new version confirms it — or, if the new
/// version never gets that far, the next start's apply step rolls it back.
/// </para>
/// <para>
/// **This service is the unprivileged side of a trust boundary, and writes almost nothing.** The request it
/// leaves is a version and who asked (<see cref="PendingUpdate"/>); the step that runs as root looks that version
/// up in the pinned sources again, finds its own installation and downloads any package itself. The service
/// stages nothing, names no folder and no source, because anything it wrote could be written by a compromised
/// service, and root must not install from it. It still checks the version here, so an admin is told at once.
/// </para>
/// </summary>
public sealed class UpdateService(
    ApiOptions options,
    IHttpClientFactory httpClientFactory,
    UpdateHostFacts facts,
    UpdateDrainState drain,
    IUpdateWorkProbe work,
    IUpdateRestart restart,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateService> logger)
{
    /// <summary>The exit code that means "restart me so an update can be applied" — the unit treats it as a
    /// clean stop that is nonetheless restarted (<c>SuccessExitStatus</c> + <c>RestartForceExitStatus</c>).</summary>
    public const int RestartExitCode = 75;

    private readonly UpdateStateStore _store = new(new UpdateWorkspace(options.RepoRoot));
    private readonly object _gate = new();
    private bool _requesting;

    public UpdateStateStore Store => _store;

    /// <summary>Set once an update has wound work down and asked the host to stop. <c>ServeCommand</c> returns
    /// it as the process's exit code.</summary>
    public int? RequestedExitCode { get; private set; }

    /// <summary>The wind-down and restart of an accepted request; awaited by tests, ignored by the API.</summary>
    public Task RestartTask { get; private set; } = Task.CompletedTask;

    /// <summary>How often the drain looks at whether work has finished.</summary>
    internal TimeSpan DrainPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public string? RunningVersion => facts.RunningVersion;

    /// <summary>Where the privileged step logs what it did — in root's directory on Linux, which the service cannot
    /// write, so this is only ever a path to tell an admin about.</summary>
    public string LogPath => facts.IsLinux
        ? UpdateWorkspace.DefaultPrivilegedDirectory + "/update.log" // a Linux path shown as text, whatever host builds it
        : Path.Combine(_store.Workspace.Directory, "update.log");

    public bool IsChannelEnabled(ReleaseChannel channel) => options.SelfUpdateChannels.Contains(channel);

    // --- what is possible here ---------------------------------------------------------------------------

    /// <summary>Whether this installation can apply an update, and if not, the reason in a sentence an admin can
    /// act on. Checked in the order an admin would fix things.</summary>
    public UpdateCapability Capability()
    {
        if (options.SelfUpdateMode == UpdatesMode.Disabled)
            return new UpdateCapability(false, false, "Updating from the console is turned off. Set DbDataSync:Updates:Mode to manual.");

        if (facts.IsWindows)
        {
            // Off until it has been watched working on a real Windows host: a running dbdatasync.exe and its
            // service hold their own files open, so the swap needs a helper that outlives them.
            return new UpdateCapability(true, false,
                "Updating from the console is not available on Windows yet. Run `dbdatasync update` on the server for the commands.");
        }

        if (!facts.IsLinux)
            return new UpdateCapability(true, false, "Updating from the console is only available on Linux.");

        if (facts.Location.Kind == InstallKind.Container)
            return new UpdateCapability(true, false, "This is a container image. Update it by pulling a newer image tag and recreating the container.");

        if (facts.Location.Kind == InstallKind.NotAToolInstall)
            return new UpdateCapability(true, false, "This copy was not installed as a dotnet tool (a development build or unpacked binaries), so there is nothing to update in place.");

        if (!facts.RunsUnderSelfUpdateUnit)
        {
            return new UpdateCapability(true, false,
                "This is not running under a systemd unit that applies updates. Run `sudo dbdatasync service install --self-update`, then restart the service. " +
                "If it is not run as a service at all, use `dbdatasync update` on the server.");
        }

        return new UpdateCapability(true, true, null);
    }

    public UpdateStatusResponse Status()
    {
        var capability = Capability();
        var state = _store.ReadState();
        var current = state.Current;

        return new UpdateStatusResponse(
            capability.Enabled,
            facts.RunningVersion,
            facts.Location.Kind.ToString(),
            options.SelfUpdateChannels.Select(c => c.ToString().ToLowerInvariant()).ToList(),
            capability.CanApply,
            capability.Reason,
            (current?.Phase ?? UpdatePhase.Idle).ToString().ToLowerInvariant(),
            current?.Message,
            current?.AtUtc,
            current?.FromVersion,
            current?.ToVersion,
            current?.RequestedBy,
            _store.ReadPending() is not null,
            IsOnTrial(),
            LogPath,
            state.History.Select(h => new UpdateHistoryEntry(
                h.Phase.ToString().ToLowerInvariant(), h.Message, h.FromVersion, h.ToVersion, h.AtUtc, h.RequestedBy)).ToList());
    }

    /// <summary>Lists releases on the enabled channels (or one of them), newest first. A channel that cannot be
    /// read is reported in <paramref name="warnings"/> as long as another can; all failing throws.</summary>
    public async Task<IReadOnlyList<ReleaseResponse>> ListReleasesAsync(
        ReleaseChannel? channel, int limit, List<string> warnings, CancellationToken cancellationToken)
    {
        var installed = ReleaseVersion.TryParse(facts.RunningVersion, out var parsed) ? parsed : null;
        var channels = channel is { } one ? [one] : options.SelfUpdateChannels;
        var catalog = NewCatalog();

        var results = new List<ReleaseResponse>();
        var failures = 0;
        foreach (var each in channels)
        {
            try
            {
                foreach (var release in await catalog.ListAsync(each, limit, cancellationToken))
                {
                    results.Add(new ReleaseResponse(
                        release.Version.Text, each.ToString().ToLowerInvariant(), release.BuiltUtc,
                        installed is not null && release.Version.Equals(installed),
                        installed is not null && release.Version > installed,
                        ReleaseUrlFor(release)));
                }
            }
            catch (ReleaseSourceException ex)
            {
                failures++;
                warnings.Add($"{each.ToString().ToLowerInvariant()}: {ex.Message}");
            }
        }

        if (failures == channels.Count && failures > 0)
            throw new ReleaseSourceException(string.Join(" ", warnings));

        return results;
    }

    /// <summary>A page a human can read about this exact release. A snapshot already carries its own
    /// GitHub Release page (<see cref="ReleaseInfo.ReleaseUrl"/>, read off the release's own <c>html_url</c>);
    /// stable/beta come from nuget.org's flat-container index, which has no such field, so that one is
    /// built from the package id and version — nuget.org's own URL scheme for a specific version page.</summary>
    private static string ReleaseUrlFor(ReleaseInfo release) =>
        release.ReleaseUrl?.ToString()
        ?? $"https://www.nuget.org/packages/{ReleaseSources.PackageId}/{release.Version.Text}";

    // --- asking for an update ----------------------------------------------------------------------------

    public async Task<UpdateRequestResult> RequestAsync(string version, string? requestedBy, CancellationToken cancellationToken)
    {
        if (options.SelfUpdateMode == UpdatesMode.Disabled)
            return Refuse(UpdateRequestOutcome.Disabled, Capability().Reason!);

        var capability = Capability();
        if (!capability.CanApply)
            return Refuse(UpdateRequestOutcome.CannotApply, capability.Reason!);

        if (!ReleaseVersion.TryParse(version, out var target) || target.Channel is not { } channel)
            return Refuse(UpdateRequestOutcome.InvalidVersion, "That is not a stable, beta or snapshot version of DbDataSync.");

        if (!options.SelfUpdateChannels.Contains(channel))
            return Refuse(UpdateRequestOutcome.ChannelNotEnabled, $"The {channel.ToString().ToLowerInvariant()} channel is not enabled (DbDataSync:Updates:Channels).");

        lock (_gate)
        {
            if (_requesting || _store.ReadPending() is not null || IsOnTrial())
                return Refuse(UpdateRequestOutcome.InProgress, "An update is already in progress.");

            _requesting = true;
        }

        var accepted = false;
        try
        {
            ReleaseInfo? release;
            try
            {
                release = (await NewCatalog().ListAsync(channel, 1000, cancellationToken)).FirstOrDefault(r => r.Version.Equals(target));
            }
            catch (ReleaseSourceException ex)
            {
                return Refuse(UpdateRequestOutcome.SourceUnavailable, ex.Message);
            }

            if (release is null)
                return Refuse(UpdateRequestOutcome.NotFound, $"{target} is not a {channel.ToString().ToLowerInvariant()} release that can be found.");

            var installed = ReleaseVersion.TryParse(facts.RunningVersion, out var running) ? running : null;
            if (UpdatePlanner.OperationFor(installed, release.Version) == PlanOperation.AlreadyInstalled)
                return Refuse(UpdateRequestOutcome.AlreadyInstalled, $"{release.Version} is already running.");

            var request = new PendingUpdate(release.Version.Text, DateTimeOffset.UtcNow, requestedBy);

            _store.WritePending(request);
            _store.Record(UpdatePhase.Draining, "Waiting for running work to finish.", installed?.Text, request.TargetVersion, requestedBy);
            drain.Begin();
            accepted = true;
            logger.LogInformation("Update to {Version} requested by {User}; winding work down.", request.TargetVersion, requestedBy);

            RestartTask = Task.Run(() => DrainThenRestartAsync(request));
            return new UpdateRequestResult(UpdateRequestOutcome.Accepted, $"Updating to {request.TargetVersion}. The service will restart.");
        }
        finally
        {
            if (!accepted)
            {
                lock (_gate)
                    _requesting = false;
            }
        }
    }

    /// <summary>Waits for running work to finish (up to the drain timeout), then asks the host to stop with the
    /// restart exit code. Interrupted work is reconciled at the next start, so a timeout is survivable.</summary>
    private async Task DrainThenRestartAsync(PendingUpdate request)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow + options.SelfUpdateDrainTimeout;
            while (work.RunningWorkCount() > 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(DrainPollInterval, lifetime.ApplicationStopping);

            var stillRunning = work.RunningWorkCount();
            if (stillRunning > 0)
                logger.LogWarning("Restarting for an update with {Count} run(s) still in flight; they are reconciled at the next start.", stillRunning);

            _store.Record(UpdatePhase.Applying, $"Restarting to install {request.TargetVersion}.", facts.RunningVersion, request.TargetVersion, request.RequestedBy);
            RequestedExitCode = RestartExitCode;
            restart.StopForUpdate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Winding down for an update failed; carrying on without it.");
            Abandon(request, $"Winding down failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // The host is already stopping for some other reason; the request goes with it, and the next
            // start reports it as not applied.
        }
    }

    private void Abandon(PendingUpdate request, string message)
    {
        _store.Record(UpdatePhase.Failed, message, facts.RunningVersion, request.TargetVersion, request.RequestedBy);
        _store.ClearPending();
        drain.End();
        lock (_gate)
            _requesting = false;
    }

    /// <summary>
    /// Called once as the service starts. Anything half-finished from before is either handled by now (the
    /// apply step moved a request to "on trial") or was left behind: a request the unit never applied, or a
    /// phase that no process is carrying any more. Reported and cleared, so an admin is told and a new request
    /// is not refused as "already in progress" forever.
    /// </summary>
    public void ReconcileOnStartup()
    {
        var pending = _store.ReadPending();
        if (pending is not null)
        {
            // Why first, then the request goes: anyone looking in between sees an explanation, not a request
            // that has silently vanished.
            _store.Record(UpdatePhase.Failed,
                $"{pending.TargetVersion} was requested but not applied before this start — the service's unit does not apply updates. " +
                "Run `sudo dbdatasync service install --self-update` and restart the service.",
                facts.RunningVersion, pending.TargetVersion, pending.RequestedBy);
            _store.ClearPending();
            return;
        }

        // "Restarting" is an update on trial — the apply step's record, waiting for this version to prove itself —
        // and is left alone. Any other phase still open belongs to a process that is no longer there.
        var current = _store.ReadState().Current;
        if (current is { IsTerminal: false } and not { Phase: UpdatePhase.Restarting })
            _store.Record(UpdatePhase.Failed, "The update was interrupted before it finished.", current.FromVersion, current.ToVersion, current.RequestedBy);
    }

    /// <summary>An update has been applied by the privileged step and this version has not yet confirmed it. Read
    /// from the display state that step wrote — the record it actually acts on is in a directory this service
    /// cannot read, and does not need to.</summary>
    private bool IsOnTrial() => _store.ReadState().Current is { Phase: UpdatePhase.Restarting };

    private string UserAgent => $"DbDataSync-update/{(ReleaseVersion.TryParse(facts.RunningVersion, out var v) ? v.Text : "unknown")}";

    private ReleaseCatalog NewCatalog() => new(
        httpClientFactory.CreateClient(),
        Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN"),
        UserAgent);

    private static UpdateRequestResult Refuse(UpdateRequestOutcome outcome, string message) => new(outcome, message);
}
