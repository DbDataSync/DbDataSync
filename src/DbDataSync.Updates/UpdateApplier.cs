using System.Diagnostics;
using System.Globalization;

namespace DbDataSync.Updates;

/// <param name="Output">Standard output and error together, for the log.</param>
public sealed record ToolCommandResult(int ExitCode, string Output);

/// <summary>Runs <c>dotnet &lt;arguments&gt;</c>. An interface so applying an update can be tested without
/// touching a real installation.</summary>
public interface IToolCommandRunner
{
    Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public enum ApplyOutcome
{
    NothingToDo,
    Applied,
    RolledBack,
    Failed,
}

public sealed record ApplyResult(ApplyOutcome Outcome, string Message);

/// <summary>
/// Everything the privileged step knows **from its own, not from the request**: where it is installed, what
/// version it is, the pinned release sources, and a stager that downloads from them.
/// </summary>
/// <param name="Location">Found from the privileged process's own assembly directory.</param>
/// <param name="RunningVersion">This binary's own version — which is what is installed.</param>
public sealed record PrivilegedContext(InstallLocation Location, string? RunningVersion, ReleaseCatalog Catalog, SnapshotStager Stager);

/// <summary>
/// Carries out an update that has been requested, and undoes one that did not take — the two halves of what
/// phase 158 leaves to the operator.
/// <para>
/// **Start-counting, not supervising.** <see cref="ApplyPendingAsync"/> is run *before* each start of the
/// service (systemd's <c>ExecStartPre=+</c>, checked in phase 159's spike). It applies a pending update and
/// records it as applied-but-unconfirmed. If the next start finds an update in that state, the version it put
/// there never proved itself — it crashed, or hung until systemd killed it — so it rolls back instead of
/// applying again. Nothing watches anything; the restart the service manager already does is the timer.
/// </para>
/// <para>
/// **What "proved itself" means** is <see cref="ConfirmAsync"/>, called by the new version once it has been
/// serving for a while, not merely when it is ready — a version that starts and dies ten seconds later must
/// still be rolled back.
/// </para>
/// <para>
/// It never throws for an update that goes wrong: it records the failure and returns it, because the caller is
/// a step *before starting a service* and a failure there must not stop the service starting on whatever is
/// installed.
/// </para>
/// <para>
/// **The privileged step trusts almost nothing.** <see cref="ApplyPendingAsync"/> runs as root while the service
/// that wrote the request runs as its own user and may be compromised, so it reads one string from the request —
/// the version — and finds everything else out for itself: which installation it is (its own location), what is
/// running (its own version), whether the version exists (the pinned release sources), and the package (downloaded
/// by it, into a directory only it can use). See <see cref="PrivilegedContext"/>.
/// </para>
/// </summary>
public sealed class UpdateApplier(UpdateStateStore store, IToolCommandRunner runner)
{
    private UpdateWorkspace Workspace => store.Workspace;

    /// <summary>Where the commands run and their output are written, for an operator to read.</summary>
    public string LogPath => Workspace.LogPath;

    /// <summary>
    /// The unit's step before every start. First settles an update already applied — the new version confirmed
    /// itself (drop the spare copies) or did not (put the previous version back) — then, if the service has asked
    /// for another, applies it.
    /// </summary>
    public async Task<ApplyResult> ApplyPendingAsync(PrivilegedContext context, CancellationToken cancellationToken)
    {
        var applied = store.ReadApplied();
        if (applied is not null)
        {
            var confirmed = store.ReadConfirmed();
            if (confirmed is not null && SameVersion(confirmed.TargetVersion, applied.TargetVersion))
            {
                Settle(applied);
            }
            else
            {
                store.ClearConfirmed();
                return await RollBackAsync(applied, "the new version did not become healthy", cancellationToken);
            }
        }

        var pending = store.ReadPending();
        if (pending is null)
            return new ApplyResult(ApplyOutcome.NothingToDo, "No update is pending.");

        // Everything below is derived here, not read from the request.
        var requestedBy = UpdateStateStore.Clean(pending.RequestedBy);
        var running = ReleaseVersion.TryParse(context.RunningVersion, out var runningVersion) ? runningVersion : null;

        if (!ReleaseVersion.TryParse(pending.TargetVersion, out var target) || target.Channel is not { } channel)
            return Refuse(running, pending.TargetVersion, requestedBy, "The requested version is not a stable, beta or snapshot version of DbDataSync.");

        if (context.Location.Kind is not (InstallKind.Global or InstallKind.ToolPath))
            return Refuse(running, target.Text, requestedBy, "This installation is not a dotnet tool install, so there is nothing to update in place.");

        ReleaseInfo? release;
        try
        {
            release = (await context.Catalog.ListAsync(channel, 1000, cancellationToken)).FirstOrDefault(r => r.Version.Equals(target));
        }
        catch (ReleaseSourceException ex)
        {
            return Refuse(running, target.Text, requestedBy, $"Could not read the release sources: {ex.Message}");
        }

        if (release is null)
            return Refuse(running, target.Text, requestedBy, $"{target} is not a {channel.ToString().ToLowerInvariant()} release that can be found.");

        if (UpdatePlanner.OperationFor(running, release.Version) == PlanOperation.AlreadyInstalled)
        {
            store.ClearPending();
            store.Record(UpdatePhase.Succeeded, $"{release.Version} was already installed.", running?.Text, release.Version.Text, requestedBy);
            return new ApplyResult(ApplyOutcome.NothingToDo, "Already installed.");
        }

        // A snapshot has no feed, so it is downloaded — by this step, from the pinned source, into a directory
        // only this process can use. Never from anything the service staged.
        DirectoryInfo? download = null;
        try
        {
            string? sourceDirectory = null;
            if (release.Channel == ReleaseChannel.Snapshot)
            {
                download = System.IO.Directory.CreateTempSubdirectory("dbdatasync-update-");
                try
                {
                    sourceDirectory = (await context.Stager.StageAsync(release, download.FullName, cancellationToken)).Directory;
                }
                catch (ReleaseSourceException ex)
                {
                    return Refuse(running, target.Text, requestedBy, ex.Message);
                }
            }

            var request = new UpdateRequest(
                release.Version.Text, running?.Text, release.Channel, context.Location.Kind, context.Location.ToolRoot!,
                sourceDirectory, DateTimeOffset.UtcNow, requestedBy);

            var result = await InstallAsync(request, cancellationToken);
            if (result.Outcome != ApplyOutcome.Applied)
            {
                store.ClearPending();
                return result;
            }

            // Applied, and now on trial. The record has no source folder: it is deleted below, and rolling back
            // uses the kept package, never it.
            store.WriteApplied(request with { SourceDirectory = null });
            store.ClearPending();
            return result;
        }
        finally
        {
            DeleteDirectory(download?.FullName);
        }
    }

    private ApplyResult Refuse(ReleaseVersion? running, string target, string? requestedBy, string message)
    {
        store.ClearPending();
        store.Record(UpdatePhase.Failed, message, running?.Text, target, requestedBy);
        Log("refused: " + message);
        return new ApplyResult(ApplyOutcome.Failed, message);
    }

    /// <summary>The new version confirmed itself: the update stands, and the spare copies are no longer needed.</summary>
    private void Settle(UpdateRequest applied)
    {
        store.ClearApplied();
        store.ClearConfirmed();
        DeleteDirectory(Workspace.RollbackDirectory);
        if (store.ReadState().Current is not { Phase: UpdatePhase.Succeeded })
            store.Record(UpdatePhase.Succeeded, $"Updated to {applied.TargetVersion}.", applied.PreviousVersion, applied.TargetVersion, applied.RequestedBy);
        Log($"confirmed {applied.TargetVersion}");
    }

    private static bool SameVersion(string a, string b) =>
        ReleaseVersion.TryParse(a, out var left) && ReleaseVersion.TryParse(b, out var right) && left.Equals(right);

    /// <summary>
    /// Installs an update **now**, for a caller that will supervise the result itself — the CLI, which stops
    /// the service, calls this, starts it and checks it is healthy, and calls <see cref="RollBackAsync"/> if not.
    /// Nothing is left "on trial", because nothing else is going to start the service and judge it: a leftover
    /// applied record would make the service's own next start roll the update straight back.
    /// </summary>
    public Task<ApplyResult> ApplyNowAsync(UpdateRequest request, CancellationToken cancellationToken) =>
        InstallAsync(request, cancellationToken);

    /// <summary>The caller has watched the new version prove itself: record the outcome and drop the spare
    /// copies. The counterpart of <see cref="ConfirmAsync"/> for a caller that applied with
    /// <see cref="ApplyNowAsync"/>.</summary>
    public void Complete(UpdateRequest request)
    {
        store.ClearApplied();
        DeleteDirectory(Workspace.RollbackDirectory);
        DeleteDirectory(Workspace.StagedDirectory);
        store.Record(UpdatePhase.Succeeded, $"Updated to {request.TargetVersion}.", request.PreviousVersion, request.TargetVersion, request.RequestedBy);
        Log($"completed {request.TargetVersion}");
    }

    private async Task<ApplyResult> InstallAsync(UpdateRequest pending, CancellationToken cancellationToken)
    {
        var from = pending.PreviousVersion;
        store.Record(UpdatePhase.Applying, $"Installing {pending.TargetVersion}.", from, pending.TargetVersion, pending.RequestedBy);
        Log($"applying {from ?? "?"} -> {pending.TargetVersion} ({pending.TargetChannel}) into {pending.ToolRoot}");

        UpdatePlan plan;
        try
        {
            plan = pending.ToPlan();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Fail(pending, $"The pending update could not be understood: {ex.Message}");
        }

        if (plan.Operation == PlanOperation.AlreadyInstalled)
        {
            store.ClearPending();
            store.Record(UpdatePhase.Succeeded, $"{pending.TargetVersion} was already installed.", from, pending.TargetVersion, pending.RequestedBy);
            return new ApplyResult(ApplyOutcome.NothingToDo, "Already installed.");
        }

        var rollbackPackage = KeepRollbackPackage(pending);
        if (rollbackPackage is null && !CanRollBackFromNuGet(pending.PreviousVersion))
        {
            return Fail(pending,
                $"Not applied: the installed package for {pending.PreviousVersion ?? "the current version"} could not be found to keep for a " +
                "rollback, and it is not a version nuget.org could supply. Nothing was changed.");
        }

        var removed = false;
        foreach (var step in UpdateCommands.Install(plan))
        {
            var result = await RunAsync(step, cancellationToken);
            if (result.ExitCode != 0)
            {
                bool? restored = removed
                    ? await TryReinstallPreviousAsync(pending, rollbackPackage, cancellationToken)
                    : null;
                return Fail(pending,
                    $"`dotnet {string.Join(' ', step.Arguments)}` failed (exit {result.ExitCode}). " +
                    (restored switch
                    {
                        true => $"{from} was put back.",
                        false => $"Putting {from} back also failed — reinstall it by hand.",
                        null => "The installed version was left as it was.",
                    }));
            }

            removed |= step.Purpose == "remove";
        }

        store.Record(UpdatePhase.Restarting, $"{pending.TargetVersion} is installed; waiting for it to start.", from, pending.TargetVersion, pending.RequestedBy);
        Log("installed; waiting for the new version to confirm");
        return new ApplyResult(ApplyOutcome.Applied, $"Installed {pending.TargetVersion}.");
    }

    /// <summary>
    /// Called by the **service**, once the version it is running has been healthy long enough. It can only say so:
    /// it writes a note the privileged step reads at the next start, and marks the update succeeded for the
    /// console. It cannot touch the record of the update or the spare copies — those are root's.
    /// <para>
    /// Returns true if the running version is the one an update just installed, judged by the display state the
    /// privileged step wrote when it applied it. A forged note or state can at worst stop a bad version being
    /// rolled back; it cannot make anything install.
    /// </para>
    /// </summary>
    public Task<bool> ConfirmAsync(string runningVersion, CancellationToken cancellationToken)
    {
        var current = store.ReadState().Current;
        if (current is not { Phase: UpdatePhase.Restarting, ToVersion: { } to }
            || !SameVersion(runningVersion, to))
            return Task.FromResult(false);

        store.WriteConfirmed(to);
        store.Record(UpdatePhase.Succeeded, $"Updated to {to}.", current.FromVersion, to, current.RequestedBy);
        Log($"confirmed {to}");
        return Task.FromResult(true);
    }

    /// <summary>Puts the previous version back — from the package kept for the purpose if it is still there,
    /// otherwise from nuget.org when the version can come from there.</summary>
    public async Task<ApplyResult> RollBackAsync(UpdateRequest applied, string reason, CancellationToken cancellationToken)
    {
        Log($"rolling back {applied.TargetVersion} -> {applied.PreviousVersion}: {reason}");
        var package = Path.Combine(Workspace.RollbackDirectory, $"{ReleaseSources.PackageId}.{applied.PreviousVersion}.nupkg");

        if (applied.PreviousVersion is null || (!File.Exists(package) && !CanRollBackFromNuGet(applied.PreviousVersion)))
        {
            // Nothing to go back to. Clear the record so every later start is not another failed attempt.
            store.ClearApplied();
            store.Record(UpdatePhase.Failed,
                $"{applied.TargetVersion} did not become healthy and there is no package to roll back to.",
                applied.PreviousVersion, applied.TargetVersion, applied.RequestedBy);
            return new ApplyResult(ApplyOutcome.Failed, "No package to roll back to.");
        }

        var usedLocalPackage = File.Exists(package);
        foreach (var step in UpdateCommands.Rollback(applied, usedLocalPackage ? Workspace.RollbackDirectory : null))
        {
            var result = await RunAsync(step, cancellationToken);

            // The kept package could not be installed (unreadable, corrupt, gone). By now the new version has been
            // uninstalled, so leaving it at that would leave *nothing* installed and the service unable to start.
            // A stable or beta version can also come from nuget.org, so try that before giving up.
            if (result.ExitCode != 0 && step.Purpose == "install" && usedLocalPackage && CanRollBackFromNuGet(applied.PreviousVersion))
            {
                Log("the kept package could not be installed; trying nuget.org");
                result = await RunAsync(UpdateCommands.Rollback(applied, null)[^1], cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                store.ClearApplied();
                store.Record(UpdatePhase.Failed,
                    $"{applied.TargetVersion} did not become healthy, and rolling back failed at `dotnet {string.Join(' ', step.Arguments)}` " +
                    $"(exit {result.ExitCode}). Reinstall {applied.PreviousVersion} by hand.",
                    applied.PreviousVersion, applied.TargetVersion, applied.RequestedBy);
                return new ApplyResult(ApplyOutcome.Failed, "Rollback failed.");
            }
        }

        store.ClearApplied();
        store.ClearConfirmed();
        DeleteDirectory(Workspace.RollbackDirectory);
        store.Record(UpdatePhase.RolledBack,
            $"{applied.TargetVersion} did not become healthy, so {applied.PreviousVersion} was put back.",
            applied.PreviousVersion, applied.TargetVersion, applied.RequestedBy);
        return new ApplyResult(ApplyOutcome.RolledBack, $"Rolled back to {applied.PreviousVersion}.");
    }

    private async Task<bool> TryReinstallPreviousAsync(UpdateRequest pending, string? rollbackPackage, CancellationToken cancellationToken)
    {
        if (pending.PreviousVersion is null)
            return false;

        var install = UpdateCommands.Rollback(pending, rollbackPackage is null ? null : Workspace.RollbackDirectory)[^1];
        return (await RunAsync(install, cancellationToken)).ExitCode == 0;
    }

    private async Task<ToolCommandResult> RunAsync(ToolInvocation step, CancellationToken cancellationToken)
    {
        Log($"$ dotnet {string.Join(' ', step.Arguments)}");
        ToolCommandResult result;
        try
        {
            result = await runner.RunAsync(step.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // dotnet missing, not executable, out of memory: an update that could not run is a failed update,
            // not an exception out of a step that has to leave the service free to start.
            result = new ToolCommandResult(-1, ex.Message);
        }

        Log($"  exit {result.ExitCode}{(string.IsNullOrWhiteSpace(result.Output) ? "" : Environment.NewLine + result.Output.TrimEnd())}");
        return result;
    }

    /// <summary>Copies the installed package aside — the store deletes it when the version is replaced — under
    /// the canonical file name, so a folder feed finds it.</summary>
    private string? KeepRollbackPackage(UpdateRequest pending)
    {
        if (pending.PreviousVersion is null)
            return null;

        var source = ToolStore.FindInstalledNupkg(pending.ToolRoot, pending.PreviousVersion);
        if (source is null)
            return null;

        Directory.CreateDirectory(Workspace.RollbackDirectory);
        var target = Path.Combine(Workspace.RollbackDirectory, $"{ReleaseSources.PackageId}.{pending.PreviousVersion}.nupkg");
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private static bool CanRollBackFromNuGet(string? version) =>
        version is not null
        && ReleaseVersion.TryParse(version, out var parsed)
        && parsed.Channel is ReleaseChannel.Stable or ReleaseChannel.Beta;

    private ApplyResult Fail(UpdateRequest pending, string message)
    {
        store.ClearPending();
        store.Record(UpdatePhase.Failed, message, pending.PreviousVersion, pending.TargetVersion, pending.RequestedBy);
        Log("failed: " + message);
        return new ApplyResult(ApplyOutcome.Failed, message);
    }

    private void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Workspace.LogPath)!);
            File.AppendAllText(Workspace.LogPath, $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Runs the real <c>dotnet</c>. Finds it through <c>DOTNET_ROOT</c> first — the systemd unit sets that, and a
/// step run before the service starts may have no <c>PATH</c> worth trusting — and gives it a home directory
/// when the environment has none, which is normal for a service and which <c>dotnet tool</c> needs for its
/// first-run state and package cache. Runs in the workspace's privileged directory, with a NuGet configuration
/// there that pins nuget.org.
/// </summary>
public sealed class ProcessToolCommandRunner(UpdateWorkspace workspace) : IToolCommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ResolveDotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // `dotnet tool` reads nuget.config from its working directory and every parent. A service's unit runs with
        // the data directory — which the service can write — as its working directory, so left alone a compromised
        // service could plant a config there that adds a package source of its own. The privileged directory is
        // the working directory instead, and holds a config that clears every other source.
        Directory.CreateDirectory(workspace.PrivilegedDirectory);
        WritePinnedNuGetConfig(Path.Combine(workspace.PrivilegedDirectory, "nuget.config"));
        start.WorkingDirectory = workspace.PrivilegedDirectory;

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_CLI_HOME")))
        {
            Directory.CreateDirectory(workspace.DotnetHome);
            start.Environment["DOTNET_CLI_HOME"] = workspace.DotnetHome;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new ToolCommandResult(process.ExitCode, (await output) + (await error));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>Only nuget.org. Written by temp file and rename, so a planted file — or a symlink — at this path is
    /// replaced rather than written through. A snapshot's own folder is added on the command line, which adds to
    /// these sources rather than replacing them.</summary>
    internal static void WritePinnedNuGetConfig(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);
        File.Move(temp, path, overwrite: true);
    }

    private static string ResolveDotnet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
                return candidate;
        }

        return "dotnet";
    }
}
