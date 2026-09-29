using DbDataSync.Updates;

namespace DbDataSync.Cli;

/// <summary>Points a registered service at the launcher — after a pre-196L install is converted, and on
/// <c>launcher repair</c>. Behind an interface so the update flow can be tested without a service manager.</summary>
internal interface IServiceRebinder
{
    /// <returns>0 when done or when there was nothing to do.</returns>
    int Rebind(string dataRoot, string launcherPath, ServiceSituation service, TextWriter output);
}

/// <summary>
/// Systemd: rewrites the unit from the same template <c>service install</c> uses, for the same user, then reloads —
/// which also drops phase 159's <c>ExecStartPre</c> apply step from a unit that had it. Windows: <c>sc config</c> of the
/// binary path. On a standard install the executable path does not change (the launcher replaced the shim at the same
/// path), so this is mostly the unit's retired lines going.
/// </summary>
internal sealed class RealServiceRebinder(ISystemdEnvironment systemd) : IServiceRebinder
{
    public int Rebind(string dataRoot, string launcherPath, ServiceSituation service, TextWriter output)
    {
        switch (service.Manager)
        {
            case ServiceManager.Systemd:
            {
                var user = ServiceRegistration.Read(dataRoot)?.Account ?? "dbdatasync";
                try
                {
                    systemd.WriteUnitFile(SystemdService.UnitPath,
                        SystemdService.RenderUnit(launcherPath, dataRoot, user, SystemdService.ResolveDotnetRoot()));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    output.WriteLine($"  Could not rewrite {SystemdService.UnitPath}: {ex.Message}");
                    return 1;
                }

                output.WriteLine($"  Rewrote {SystemdService.UnitPath} to run {launcherPath}.");
                return systemd.RunSystemctl("daemon-reload");
            }

            case ServiceManager.WindowsService:
            {
                var exit = ServiceCommand.Sc("config", ServiceCommand.ServiceName, "binPath=", ServiceCommand.BinPath(launcherPath, dataRoot));
                if (exit == 0)
                    output.WriteLine($"  Pointed the '{ServiceCommand.ServiceName}' service at {launcherPath}.");
                return exit;
            }

            default:
                return 0;
        }
    }
}

/// <summary>
/// Puts the launcher in place (phase 196L), wherever a command needs one to exist: <c>service install</c> (so the
/// service is registered against it), <c>launcher repair</c>, and — through <see cref="SlotMigration"/> directly —
/// the first <c>update --apply</c> on a pre-196L install.
/// </summary>
internal static class LauncherSetup
{
    /// <summary>
    /// Under a launcher: re-copies the current slot's bundled launcher over the installed one (only the files that
    /// differ) and tidies what earlier runs left. From a pre-196L <c>--tool-path</c> install: converts it, loudly.
    /// Anything else (a global tool, a development build): nothing — there is no directory of ours to put one in.
    /// </summary>
    /// <returns>The launcher's path, or null when there is none (the caller carries on without one).</returns>
    public static async Task<string?> EnsureAsync(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var launcher = LauncherContext.Current();
        if (launcher is not null)
        {
            var layout = new SlotLayout(launcher.Root);
            var source = LauncherInstaller.SourceDirectory(AppContext.BaseDirectory);
            if (source is null)
            {
                error.WriteLine($"This version carries no launcher for this platform, so the installed one in {layout.Root} was left as it is.");
            }
            else
            {
                var replaced = LauncherInstaller.Install(source, layout.Root);
                output.WriteLine(replaced == 0
                    ? $"The launcher in {layout.Root} is already this version's."
                    : $"Replaced the launcher in {layout.Root} with slot {launcher.Slot}'s ({replaced} file(s)).");
            }

            LauncherInstaller.CleanUp(layout.Root);
            if (SlotMigration.RemoveLegacyStore(layout))
                output.WriteLine($"Removed {Path.Combine(layout.Root, ".store")}, left behind by the install from before versioned slots.");
            return LauncherPath(layout.Root);
        }

        var location = InstallLocator.Locate(
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"),
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true");
        if (location.Kind != InstallKind.ToolPath)
            return null;

        var running = typeof(Help).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return await ConvertAsync(location.ToolRoot!, running ?? "", AppContext.BaseDirectory, dataRoot, output, error, cancellationToken);
    }

    /// <summary>Converts a pre-196L tool-path install to slots, saying so first. Null when it could not be done;
    /// nothing is half-changed in a way the legacy install cannot keep running on (see <see cref="SlotMigration"/>).</summary>
    public static async Task<string?> ConvertAsync(
        string toolRoot, string runningVersion, string payloadDirectory, string dataRoot,
        TextWriter output, TextWriter error, CancellationToken cancellationToken, Func<string, IToolCommandRunner>? runnerFor = null)
    {
        output.WriteLine(
            $"This install ({toolRoot}) predates versioned slots; converting it: the running version becomes slot a, and the launcher " +
            "replaces the tool's own shim at the same path. This happens once.");

        var work = Directory.CreateTempSubdirectory("dbdatasync-launcher-");
        try
        {
            var runner = runnerFor?.Invoke(work.FullName) ?? new ProcessToolCommandRunner(work.FullName);
            var result = await new SlotMigration(runner, new UpdateStateStore(new UpdateWorkspace(dataRoot)))
                .ConvertAsync(toolRoot, runningVersion, payloadDirectory, cancellationToken);
            if (!result.Succeeded)
            {
                error.WriteLine(result.Message);
                return null;
            }

            output.WriteLine(result.Message);
            output.WriteLine(
                $"From now on update with `dbdatasync update`, not `dotnet tool … --tool-path {toolRoot}` — that would write a shim over the launcher.");
            return LauncherPath(toolRoot);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string LauncherPath(string root) =>
        Path.Combine(root, SlotPaths.LauncherName + (OperatingSystem.IsWindows() ? ".exe" : ""));

    /// <summary>Whether this process can write <paramref name="directory"/> — checked before an update starts, so a
    /// missing <c>sudo</c> is a sentence up front rather than a failure half way.</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// <c>dbdatasync launcher repair</c> (phase 196L) — the rare, explicit path for the launcher itself: re-copies the
/// current slot's launcher over the installed one and re-points a registered service. From a pre-196L install it
/// converts it. Ordinary updates never touch the launcher.
/// </summary>
internal static class LauncherCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "repair", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: dbdatasync launcher repair [--repo <path>]");
            return 1;
        }

        var root = DbDataSyncRoot.Resolve(args);
        var launcher = await LauncherSetup.EnsureAsync(root, Console.Out, Console.Error, CancellationToken.None);
        if (launcher is null)
        {
            Console.Error.WriteLine(
                "There is no launcher to repair: this is not a `dotnet tool install --tool-path` install (a global tool, a development " +
                "build, or a container), or converting it failed above.");
            return 1;
        }

        var service = UpdateEnvironment.RegisteredService(root);
        return new RealServiceRebinder(new RealSystemdEnvironment()).Rebind(root, launcher, service, Console.Out);
    }
}
