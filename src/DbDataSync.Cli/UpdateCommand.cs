using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DbDataSync.Core.Config;
using DbDataSync.Updates;

namespace DbDataSync.Cli;

/// <summary>
/// Everything <see cref="UpdateCommand"/> reads from the machine it runs on, gathered in one place so a test
/// can hand it a different machine — a Windows install, a container, a service under systemd — without
/// being on one.
/// </summary>
/// <param name="InstalledVersion">The running tool's informational version, or null.</param>
/// <param name="BaseDirectory">Where this tool's assemblies are; how an install is recognised (see
/// <see cref="InstallLocator"/>).</param>
/// <param name="ServiceLookup">Given the data directory, whether a service is registered against it.</param>
/// <param name="RunnerFor">Runs <c>dotnet</c>, from the working directory given (a private one per update).</param>
/// <param name="Launcher">What the launcher said about this process (phase 196L); null when it was not started by one.</param>
internal sealed record UpdateEnvironment(
    string? InstalledVersion,
    string BaseDirectory,
    string GlobalToolsDirectory,
    bool InContainer,
    bool IsWindows,
    string DefaultStageDirectory,
    Func<string, ServiceSituation> ServiceLookup,
    bool InputRedirected,
    string? GitHubToken,
    IServiceControl ServiceControl,
    IHealthProbe Health,
    Func<string, IToolCommandRunner> RunnerFor,
    string UserName,
    LauncherContext? Launcher,
    IServiceRebinder Rebinder)
{
    public static UpdateEnvironment Current() => new(
        typeof(Help).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools"),
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
        OperatingSystem.IsWindows(),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DbDataSync", "updates"),
        RegisteredService,
        Console.IsInputRedirected,
        Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN"),
        OperatingSystem.IsWindows() ? new WindowsServiceControl() : new SystemctlServiceControl(new RealSystemdEnvironment()),
        new HttpHealthProbe(),
        workingDirectory => new ProcessToolCommandRunner(workingDirectory),
        Environment.UserName,
        LauncherContext.Current(),
        new RealServiceRebinder(new RealSystemdEnvironment()));

    /// <summary>The phase 135 marker says whether <c>service install</c> ever registered a service against this
    /// data directory, and on which platform — which is what decides the stop/start steps of a plan.</summary>
    internal static ServiceSituation RegisteredService(string root) =>
        ServiceRegistration.Read(root)?.Platform switch
        {
            "windows" when OperatingSystem.IsWindows() => new ServiceSituation(ServiceManager.WindowsService, ServiceCommand.ServiceName),
            "linux" when OperatingSystem.IsLinux() => new ServiceSituation(ServiceManager.Systemd, SystemdService.UnitName),
            _ => ServiceSituation.None,
        };
}

/// <summary>
/// <c>dbdatasync update</c> — lists what can be installed, lets the operator choose, downloads a snapshot if
/// that is what was chosen, and prints the commands that install it. **It never changes the installation**:
/// the operator runs the printed commands. That is deliberate; a running tool replacing itself is a different
/// and harder problem (it cannot, on Windows, while it is running), and this command is useful without it.
/// <para>
/// Sources are fixed (<see cref="ReleaseSources"/>) and there is no option to change them.
/// </para>
/// </summary>
public static class UpdateCommand
{
    private const int DefaultLimit = 5;
    private const int DefaultHealthTimeoutSeconds = 90;
    private static readonly ReleaseChannel[] AllChannels = [ReleaseChannel.Stable, ReleaseChannel.Beta, ReleaseChannel.Snapshot];

    public static async Task<int> RunAsync(string[] args)
    {
        using var http = new HttpClient();
        return await RunAsync(
            args, UpdateEnvironment.Current(), http, Console.In, Console.Out, Console.Error, CancellationToken.None);
    }

    internal static async Task<int> RunAsync(
        string[] args, UpdateEnvironment env, HttpClient http,
        TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadOptions(args, error, out var options))
            return 1;

        if (options.Status)
        {
            WriteStatus(new UpdateStateStore(new UpdateWorkspace(DbDataSyncRoot.Resolve(args))), env, output);
            return 0;
        }

        if (options.Rollback)
            return await RollbackAsync(options, env, args, input, output, error, cancellationToken);

        var installed = ReleaseVersion.TryParse(env.InstalledVersion, out var parsed) ? parsed : null;
        var userAgent = $"DbDataSync-update/{installed?.Text ?? "unknown"}";
        var catalog = new ReleaseCatalog(http, env.GitHubToken, userAgent);

        try
        {
            return options.To is not null
                ? await ChooseByVersionAsync(options, installed, env, http, catalog, args, input, output, error, userAgent, cancellationToken)
                : await ListAndChooseAsync(options, installed, env, http, catalog, args, input, output, error, userAgent, cancellationToken);
        }
        catch (ReleaseSourceException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ListAndChooseAsync(
        Options options, ReleaseVersion? installed, UpdateEnvironment env, HttpClient http, ReleaseCatalog catalog,
        string[] args, TextReader input, TextWriter output, TextWriter error, string userAgent, CancellationToken cancellationToken)
    {
        var sections = await FetchAsync(catalog, options.Channels, options.Limit, error, cancellationToken);
        if (sections.Count == 0)
            return 1;

        var interactive = !options.List && !env.InputRedirected;
        if (options.Json)
        {
            output.WriteLine(ToJson(sections, installed));
            return 0;
        }

        var flat = sections.SelectMany(s => s.Releases).ToList();
        WriteList(output, sections, installed, numbered: interactive);
        if (!interactive)
            return 0;

        if (flat.Count == 0)
        {
            output.WriteLine("Nothing to choose from.");
            return 0;
        }

        output.WriteLine();
        output.Write("Install which one? Type a number or a version (empty to cancel): ");
        var answer = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(answer))
        {
            output.WriteLine("Cancelled.");
            return 0;
        }

        var chosen = int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (number >= 1 && number <= flat.Count ? flat[number - 1] : null)
            : flat.FirstOrDefault(r => string.Equals(r.Version.Text, answer, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            error.WriteLine($"'{answer}' is not one of the releases listed.");
            return 1;
        }

        return await PlanAsync(chosen, options, installed, env, http, args, input, output, error, userAgent, cancellationToken);
    }

    private static async Task<int> ChooseByVersionAsync(
        Options options, ReleaseVersion? installed, UpdateEnvironment env, HttpClient http, ReleaseCatalog catalog,
        string[] args, TextReader input, TextWriter output, TextWriter error, string userAgent, CancellationToken cancellationToken)
    {
        var wanted = options.To!;
        if (wanted.Channel is not { } channel)
        {
            error.WriteLine($"'{wanted}' is not a stable, beta or snapshot version of DbDataSync.");
            return 1;
        }

        // The version already running — or, under the launcher, already in the other slot — is answered without a
        // network round trip, and without needing it to still be listed: retention prunes old snapshots, so an
        // installed one can outlive its release. That is what lets `--to` go back to a pruned snapshot the other
        // slot still holds, exactly as `--rollback` would.
        var onDisk = (installed is not null && installed.Equals(wanted)) || OtherSlotHolds(env, wanted);
        var releases = onDisk
            ? []
            : await catalog.ListAsync(channel, 1000, cancellationToken);
        var chosen = onDisk
            ? new ReleaseInfo(wanted, channel)
            : releases.FirstOrDefault(r => r.Version.Equals(wanted));
        if (chosen is null)
        {
            error.WriteLine($"{wanted} is not a {channel.ToString().ToLowerInvariant()} release that can be found. Run `dbdatasync update --list` to see what is available.");
            return 1;
        }

        return await PlanAsync(chosen, options, installed, env, http, args, input, output, error, userAgent, cancellationToken);
    }

    private static async Task<int> PlanAsync(
        ReleaseInfo chosen, Options options, ReleaseVersion? installed, UpdateEnvironment env, HttpClient http,
        string[] args, TextReader input, TextWriter output, TextWriter error, string userAgent, CancellationToken cancellationToken)
    {
        var location = InstallLocator.Locate(env.BaseDirectory, env.GlobalToolsDirectory, env.InContainer);
        var operation = UpdatePlanner.OperationFor(installed, chosen.Version);

        // A snapshot the other slot already holds is installed from nowhere — the switch just flips to it — so it is
        // not downloaded again.
        var inOtherSlot = OtherSlotHolds(env, chosen.Version);
        string? stagedDirectory = null;
        if (!inOtherSlot && UpdatePlanner.NeedsStagedPackage(chosen, location, operation))
        {
            // Absolute: it ends up on a `dotnet` command line, which is run from a working directory of its own.
            var stageRoot = Path.GetFullPath(options.StageDirectory ?? env.DefaultStageDirectory);
            output.WriteLine($"Downloading {ReleaseSources.SnapshotNupkgName(chosen.Version)} …");
            var staged = await new SnapshotStager(http, userAgent).StageAsync(chosen, stageRoot, cancellationToken);
            output.WriteLine(staged.Reused
                ? $"Already staged and verified ({FormatSize(staged.SizeBytes)})."
                : $"Downloaded and verified ({FormatSize(staged.SizeBytes)}).");
            output.WriteLine();
            stagedDirectory = staged.Directory;
        }

        var root = DbDataSyncRoot.Resolve(args);
        var plan = UpdatePlanner.Build(
            installed, chosen, location, stagedDirectory,
            env.ServiceLookup(root),
            needsElevation: location.Kind == InstallKind.ToolPath && !CliOptions.IsUnderUserProfile(location.ToolRoot),
            alreadyOnDisk: inOtherSlot);

        if (!options.Apply)
        {
            if (env.Launcher is not null && plan.IsUpdatable && plan.Operation != PlanOperation.AlreadyInstalled)
            {
                // Phase 196L: the dotnet commands for this plan would write into the running slot — exactly what the
                // slots exist to avoid. The one command that does it properly is this tool's own.
                output.Write(UpdatePlanRenderer.RenderHeader(plan));
                output.WriteLine();
                output.WriteLine("Nothing has been changed. This install uses versioned slots; to install it into the other slot and switch to it:");
                output.WriteLine();
                output.WriteLine($"       {Commands(env, root).ApplyFor(plan.Target.Version.Text)}");
                return 0;
            }

            output.Write(UpdatePlanRenderer.Render(plan, env.IsWindows));
            if (plan.IsUpdatable && plan.Location.Kind == InstallKind.ToolPath && plan.Operation != PlanOperation.AlreadyInstalled)
            {
                output.WriteLine();
                output.WriteLine("Or let dbdatasync do all of that, converting this install to versioned slots first (once):");
                output.WriteLine($"       {Commands(env, root).ApplyFor(plan.Target.Version.Text)}");
            }

            return 0;
        }

        return await ApplyAsync(plan, options, env, args, root, input, output, error, cancellationToken);
    }

    /// <summary><c>--apply</c>: carry the plan out rather than print it.</summary>
    private static async Task<int> ApplyAsync(
        UpdatePlan plan, Options options, UpdateEnvironment env, string[] args, string root,
        TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        output.Write(UpdatePlanRenderer.RenderHeader(plan));
        output.WriteLine();

        if (plan.Operation == PlanOperation.AlreadyInstalled)
        {
            output.WriteLine($"{plan.Target.Version} is already installed — nothing to do.");
            return 0;
        }

        if (!plan.IsUpdatable)
        {
            // The same explanation the printed plan gives: a development build or a container has no tool
            // install to update in place.
            output.Write(UpdatePlanRenderer.Render(plan, env.IsWindows).Split("\n\n", 2)[^1]);
            return 1;
        }

        if (plan.Location.Kind == InstallKind.Global)
        {
            // Phase 196L: a global tool's directory belongs to `dotnet tool` — its shim, its manifest — and a launcher
            // cannot live beside them. The printed commands still work for an install the user owns.
            error.WriteLine(
                "A global tool (`dotnet tool install -g`) cannot switch between versioned slots, so --apply is not available for it. " +
                "Run the commands below instead — or install machine-wide (docs/install.md) to get --apply and --rollback.");
            error.WriteLine();
            output.Write(UpdatePlanRenderer.Render(plan, env.IsWindows).Split("\n\n", 2)[^1]);
            return 1;
        }

        var toolRoot = env.Launcher?.Root ?? plan.Location.ToolRoot!;
        if (!LauncherSetup.CanWrite(toolRoot))
        {
            error.WriteLine($"Cannot write {toolRoot}, so nothing was changed. {Elevation(env)}");
            return 1;
        }

        if (!await ConfirmAsync(plan.Service, "Apply this update now?", options, env, input, output, error, cancellationToken))
            return options.Yes || !env.InputRedirected ? 0 : 1;

        var store = new UpdateStateStore(new UpdateWorkspace(root));
        var work = Directory.CreateTempSubdirectory("dbdatasync-update-");
        try
        {
            SlotLayout layout;
            if (env.Launcher is null)
            {
                // Phase 196L: a pre-slot --tool-path install converts itself, once, before its first update — loudly.
                if (plan.Installed is null)
                {
                    error.WriteLine("The running version could not be read, so this install cannot be converted to versioned slots.");
                    return 1;
                }

                var launcherPath = await LauncherSetup.ConvertAsync(
                    toolRoot, plan.Installed.Text, env.BaseDirectory, root, output, error, cancellationToken, env.RunnerFor);
                if (launcherPath is null)
                    return 1;

                if (plan.Service.Manager != ServiceManager.None && env.Rebinder.Rebind(root, launcherPath, plan.Service, output) != 0)
                    error.WriteLine("The service could not be pointed at the launcher; run `dbdatasync launcher repair` after this.");
                output.WriteLine();
                layout = new SlotLayout(toolRoot);
            }
            else
            {
                layout = new SlotLayout(toolRoot);
                LauncherInstaller.CleanUp(layout.Root);
                if (SlotMigration.RemoveLegacyStore(layout))
                    output.WriteLine($"Removed {Path.Combine(layout.Root, ".store")}, left behind by the install from before versioned slots.");
            }

            var from = layout.Current ?? env.Launcher?.Slot ?? SlotPaths.SlotA;
            var change = new SlotSwitch(
                layout, from, SlotPaths.Other(from), plan.Installed?.Text, plan.Target.Version.Text,
                plan.SourceDirectory, Install: true, env.UserName);
            return await UpdateApplyFlow.RunAsync(
                change, plan.Service, store, env.RunnerFor(work.FullName), env.ServiceControl, env.Health,
                HealthUrl(options, root), options.HealthTimeout, output, error, cancellationToken);
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

    /// <summary><c>--rollback</c>: switch to the other slot — the same flow as an update, without the install.</summary>
    private static async Task<int> RollbackAsync(
        Options options, UpdateEnvironment env, string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var root = DbDataSyncRoot.Resolve(args);
        if (env.Launcher is null)
        {
            error.WriteLine(
                "This install has no versioned slots, so there is nothing to switch back to. The first `dbdatasync update --apply` " +
                "converts a machine-wide install; from then on the version before each update is kept.");
            return 1;
        }

        var layout = new SlotLayout(env.Launcher.Root);
        var from = layout.Current ?? env.Launcher.Slot;
        var target = layout.Slot(SlotPaths.Other(from));
        if (target.Version is null)
        {
            error.WriteLine(target.Ambiguous
                ? $"Slot {target.Slot} holds more than one install, so it cannot be switched to."
                : $"Slot {target.Slot} is empty — there is no earlier version to switch back to.");
            return 1;
        }

        var running = layout.Slot(from).Version;
        output.WriteLine($"Running     {running ?? "(unknown)"}  (slot {from})");
        output.WriteLine($"Switch to   {target.Version}  (slot {target.Slot})");
        output.WriteLine();

        if (!LauncherSetup.CanWrite(layout.Root))
        {
            error.WriteLine($"Cannot write {layout.Root}, so nothing was changed. {Elevation(env)}");
            return 1;
        }

        var service = env.ServiceLookup(root);
        if (!await ConfirmAsync(service, "Switch back now?", options, env, input, output, error, cancellationToken))
            return options.Yes || !env.InputRedirected ? 0 : 1;

        var change = new SlotSwitch(layout, from, target.Slot, running, target.Version, null, Install: false, env.UserName);
        return await UpdateApplyFlow.RunAsync(
            change, service, new UpdateStateStore(new UpdateWorkspace(root)), env.RunnerFor(Path.GetTempPath()), env.ServiceControl,
            env.Health, HealthUrl(options, root), options.HealthTimeout, output, error, cancellationToken);
    }

    /// <summary>Asks before stopping anything. False means do not go on: cancelled at the prompt (a clean exit), or no
    /// terminal to ask on and no <c>--yes</c> (an error, already reported).</summary>
    private static async Task<bool> ConfirmAsync(
        ServiceSituation service, string question, Options options, UpdateEnvironment env,
        TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (options.Yes)
            return true;

        if (env.InputRedirected)
        {
            error.WriteLine("This stops and restarts the service. Pass --yes to do that without a prompt.");
            return false;
        }

        output.Write(service.Manager == ServiceManager.None
            ? $"{question} [y/N] "
            : $"{question} The service will be stopped and started again. [y/N] ");
        var answer = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            return true;

        output.WriteLine("Cancelled.");
        return false;
    }

    /// <summary>Under the launcher: whether the slot that is not current already holds <paramref name="version"/>.</summary>
    private static bool OtherSlotHolds(UpdateEnvironment env, ReleaseVersion version)
    {
        if (env.Launcher is not { } launcher)
            return false;

        var layout = new SlotLayout(launcher.Root);
        var current = layout.Current ?? launcher.Slot;
        return UpdateCommands.SlotHolds(layout.Slot(SlotPaths.Other(current)).Version, version.Text);
    }

    private static string HealthUrl(Options options, string root) =>
        options.Url
        ?? DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:Url")
        ?? "http://localhost:5080";

    private static string Elevation(UpdateEnvironment env) =>
        env.IsWindows ? "Run it from an elevated prompt (Run as administrator)." : "Run it with sudo.";

    private static UpdateCliCommands Commands(UpdateEnvironment env, string root) =>
        UpdateCliCommands.For(env.IsWindows, string.Equals(Path.GetFullPath(root), Path.GetFullPath(CliOptions.DefaultRoot), StringComparison.Ordinal) ? null : root);

    private static void WriteStatus(UpdateStateStore store, UpdateEnvironment env, TextWriter output)
    {
        if (env.Launcher is { } launcher)
        {
            var layout = new SlotLayout(launcher.Root);
            var current = layout.Current;
            output.WriteLine($"Slots ({layout.Root})");
            foreach (var slot in new[] { SlotPaths.SlotA, SlotPaths.SlotB })
            {
                var held = layout.Slot(slot);
                var holds = held.Ambiguous ? "(more than one install)" : held.Version ?? "(empty)";
                output.WriteLine($"  {slot}  {holds}{(slot == current ? "  current" : "")}");
            }

            foreach (var check in layout.Check())
                output.WriteLine($"  {check.Level.ToString().ToLowerInvariant()}: {check.Message}");
            output.WriteLine();
        }
        else if (InstallLocator.Locate(env.BaseDirectory, env.GlobalToolsDirectory, env.InContainer).Kind == InstallKind.ToolPath)
        {
            output.WriteLine("Slots: none yet — this install predates them. The first `dbdatasync update --apply` converts it.");
            output.WriteLine();
        }

        output.WriteLine($"Update state ({store.Workspace.Directory})");
        var state = store.ReadState();
        if (state.Current is null)
        {
            output.WriteLine("  no update has been attempted here.");
            return;
        }

        output.WriteLine($"  now: {Describe(state.Current)}");
        if (state.History.Count > 0)
        {
            output.WriteLine("  history, newest first:");
            foreach (var entry in state.History)
                output.WriteLine($"    {Describe(entry)}");
        }
    }

    private static string Describe(UpdateProgress progress) =>
        $"{progress.AtUtc:yyyy-MM-dd HH:mm} UTC  {progress.Phase.ToString().ToLowerInvariant(),-10}  " +
        $"{progress.FromVersion ?? "?"} -> {progress.ToVersion ?? "?"}" +
        (string.IsNullOrEmpty(progress.Message) ? "" : $"  {progress.Message}");

    private static async Task<List<Section>> FetchAsync(
        ReleaseCatalog catalog, IReadOnlyList<ReleaseChannel> channels, int limit, TextWriter error, CancellationToken cancellationToken)
    {
        var fetches = channels
            .Select(async channel =>
            {
                try
                {
                    return (channel, releases: await catalog.ListAsync(channel, limit, cancellationToken), failure: (string?)null);
                }
                catch (ReleaseSourceException ex)
                {
                    return (channel, releases: (IReadOnlyList<ReleaseInfo>)[], failure: ex.Message);
                }
            })
            .ToList();

        var sections = new List<Section>();
        foreach (var (channel, releases, failure) in await Task.WhenAll(fetches))
        {
            if (failure is not null)
                error.WriteLine($"{channel.ToString().ToLowerInvariant()}: {failure}");
            else
                sections.Add(new Section(channel, releases));
        }

        // Some channels failing is a warning as long as another answered; nothing answering is a failure.
        return sections;
    }

    private static void WriteList(TextWriter output, List<Section> sections, ReleaseVersion? installed, bool numbered)
    {
        var width = sections.SelectMany(s => s.Releases).Select(r => r.Version.Text.Length).DefaultIfEmpty(0).Max();
        var number = 0;

        output.WriteLine($"Installed: {installed?.ToString() ?? "(unknown)"}");
        foreach (var section in sections)
        {
            output.WriteLine();
            output.WriteLine(section.Channel.ToString().ToLowerInvariant());
            if (section.Releases.Count == 0)
                output.WriteLine("  (none)");

            foreach (var release in section.Releases)
            {
                var prefix = numbered ? $"{++number,3}) " : "  ";
                var built = release.BuiltUtc is { } builtAt ? $"built {builtAt:yyyy-MM-dd HH:mm} UTC" : "";
                var mark = installed is null ? "" : release.Version.Equals(installed) ? "  installed" : release.Version > installed ? "  newer" : "";
                output.WriteLine($"{prefix}{release.Version.Text.PadRight(width)}  {built}{mark}".TrimEnd());
            }
        }
    }

    private static string ToJson(List<Section> sections, ReleaseVersion? installed) =>
        JsonSerializer.Serialize(
            sections.SelectMany(s => s.Releases).Select(r => new
            {
                channel = r.Channel.ToString().ToLowerInvariant(),
                version = r.Version.Text,
                builtUtc = r.BuiltUtc,
                installed = installed is not null && r.Version.Equals(installed),
                newer = installed is not null && r.Version > installed,
            }),
            new JsonSerializerOptions { WriteIndented = true });

    private static string FormatSize(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static bool TryReadOptions(string[] args, TextWriter error, out Options options)
    {
        options = new Options();

        var channelText = CliOptions.Read(args, "--channel");
        var channels = AllChannels;
        if (channelText is not null && !string.Equals(channelText, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!channelText.All(char.IsAsciiLetter) || !Enum.TryParse<ReleaseChannel>(channelText, ignoreCase: true, out var channel))
            {
                error.WriteLine($"Unknown channel '{channelText}'. Use stable, beta, snapshot or all.");
                return false;
            }

            channels = [channel];
        }

        var limit = DefaultLimit;
        var limitText = CliOptions.Read(args, "--limit");
        if (limitText is not null && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            error.WriteLine($"--limit must be a whole number of at least 1, not '{limitText}'.");
            return false;
        }

        var healthSeconds = DefaultHealthTimeoutSeconds;
        var healthText = CliOptions.Read(args, "--health-timeout");
        if (healthText is not null
            && (!int.TryParse(healthText, NumberStyles.None, CultureInfo.InvariantCulture, out healthSeconds) || healthSeconds < 1))
        {
            error.WriteLine($"--health-timeout must be a whole number of seconds, at least 1, not '{healthText}'.");
            return false;
        }

        ReleaseVersion? to = null;
        var toText = CliOptions.Read(args, "--to");
        if (toText is not null && !ReleaseVersion.TryParse(toText, out to))
        {
            error.WriteLine($"'{toText}' is not a DbDataSync version.");
            return false;
        }

        options = new Options
        {
            Channels = channels,
            Limit = limit,
            To = to,
            List = CliOptions.Has(args, "--list"),
            Json = CliOptions.Has(args, "--json"),
            StageDirectory = CliOptions.Read(args, "--stage-dir"),
            Apply = CliOptions.Has(args, "--apply"),
            Yes = CliOptions.Has(args, "--yes"),
            Status = CliOptions.Has(args, "--status"),
            Rollback = CliOptions.Has(args, "--rollback"),
            Url = CliOptions.Read(args, "--url"),
            HealthTimeout = TimeSpan.FromSeconds(healthSeconds),
        };
        return true;
    }

    private sealed record Section(ReleaseChannel Channel, IReadOnlyList<ReleaseInfo> Releases);

    private sealed record Options
    {
        public IReadOnlyList<ReleaseChannel> Channels { get; init; } = AllChannels;
        public int Limit { get; init; } = DefaultLimit;
        public ReleaseVersion? To { get; init; }
        public bool List { get; init; }
        public bool Json { get; init; }
        public string? StageDirectory { get; init; }
        public bool Apply { get; init; }
        public bool Yes { get; init; }
        public bool Status { get; init; }
        public bool Rollback { get; init; }
        public string? Url { get; init; }
        public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHealthTimeoutSeconds);
    }
}
