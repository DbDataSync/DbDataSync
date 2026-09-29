namespace DbDataSync.Updates;

public sealed record SlotInstallResult(bool Succeeded, string Message);

/// <summary>
/// Puts a version into a slot that is not running (phase 196L) — the only step of an update that touches files, and
/// it never touches the ones in use: the current slot, and the launcher, are left alone.
/// </summary>
public sealed class SlotInstaller(IToolCommandRunner runner, UpdateStateStore store)
{
    /// <param name="sourceDirectory">A staged snapshot's folder, or a folder holding a package already on disk; null
    /// for a stable or beta version, which nuget.org serves.</param>
    public async Task<SlotInstallResult> InstallAsync(
        SlotLayout layout, string slot, string version, string? sourceDirectory, CancellationToken cancellationToken)
    {
        var state = layout.Slot(slot);
        if (UpdateCommands.SlotHolds(state.Version, version))
        {
            store.Log($"slot {slot} already holds {version}");
            return new SlotInstallResult(true, $"Slot {slot} already holds {version}; nothing to download.");
        }

        // Emptied rather than uninstalled: see UpdateCommands.IntoEmptySlot.
        try
        {
            if (Directory.Exists(state.Directory))
                Directory.Delete(state.Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            store.Log($"could not clear slot {slot}: {ex.Message}");
            return new SlotInstallResult(false,
                $"Slot {slot} ({state.Directory}) could not be cleared: {ex.Message} " +
                (OperatingSystem.IsWindows()
                    ? "Usually a dbdatasync started before the last update is still running from it — close it and run this again. "
                    : "") +
                "Nothing was switched.");
        }

        var step = UpdateCommands.IntoEmptySlot(state.Directory, version, sourceDirectory);
        store.Log($"$ dotnet {string.Join(' ', step.Arguments)}");
        ToolCommandResult result;
        try
        {
            result = await runner.RunAsync(step.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ToolCommandResult(-1, ex.Message);
        }

        store.Log($"  exit {result.ExitCode}{(string.IsNullOrWhiteSpace(result.Output) ? "" : Environment.NewLine + result.Output.TrimEnd())}");
        if (result.ExitCode != 0)
        {
            return new SlotInstallResult(false,
                $"`dotnet {string.Join(' ', step.Arguments)}` failed (exit {result.ExitCode}). Nothing was switched; details are in {store.Workspace.LogPath}.");
        }

        // What the launcher will look for, checked before anything depends on it.
        var installed = layout.Slot(slot);
        if (!UpdateCommands.SlotHolds(installed.Version, version))
        {
            return new SlotInstallResult(false,
                $"`dotnet tool install` succeeded but slot {slot} does not hold a runnable {version} " +
                $"({(installed.Ambiguous ? "more than one install" : installed.Version ?? "nothing")} found). Nothing was switched.");
        }

        return new SlotInstallResult(true, $"Installed {version} into slot {slot}.");
    }
}
