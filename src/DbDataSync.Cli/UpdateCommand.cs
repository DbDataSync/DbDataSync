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
    Func<UpdateWorkspace, IToolCommandRunner> RunnerFor,
    string UserName)
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
        new SystemctlServiceControl(new RealSystemdEnvironment()),
        new HttpHealthProbe(),
        workspace => new ProcessToolCommandRunner(workspace),
        Environment.UserName);

    /// <summary>The phase 135 marker says whether <c>service install</c> ever registered a service against this
    /// data directory, and on which platform — which is what decides the stop/start steps of a plan.</summary>
    private static ServiceSituation RegisteredService(string root) =>
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
            WriteStatus(new UpdateStateStore(new UpdateWorkspace(DbDataSyncRoot.Resolve(args))), output);
            return 0;
        }

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

        // The version already running is answered without a network round trip — and without needing it to
        // still be listed: retention prunes old snapshots, so an installed one can outlive its release.
        var releases = installed is not null && installed.Equals(wanted)
            ? []
            : await catalog.ListAsync(channel, 1000, cancellationToken);
        var chosen = installed is not null && installed.Equals(wanted)
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

        string? stagedDirectory = null;
        if (UpdatePlanner.NeedsStagedPackage(chosen, location, operation))
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
            needsElevation: location.Kind == InstallKind.ToolPath && !CliOptions.IsUnderUserProfile(location.ToolRoot));

        if (!options.Apply)
        {
            output.Write(UpdatePlanRenderer.Render(plan, env.IsWindows));
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

        if (env.IsWindows)
        {
            // Not "not implemented" — deliberately off until it has been watched working on a real Windows
            // host. A running dbdatasync.exe (and the service) hold their own files open, so the swap needs a
            // helper that outlives them, and that has not been verified. The printed plan is the way.
            error.WriteLine("Applying an update automatically is not available on Windows yet. Run the commands below instead.");
            error.WriteLine();
            output.Write(UpdatePlanRenderer.Render(plan, env.IsWindows).Split("\n\n", 2)[^1]);
            return 1;
        }

        if (!options.Yes)
        {
            if (env.InputRedirected)
            {
                error.WriteLine("Applying an update stops and restarts the service. Pass --yes to do that without a prompt.");
                return 1;
            }

            output.Write(plan.Service.Manager == ServiceManager.None
                ? "Apply this update now? [y/N] "
                : "Apply this update now? The service will be stopped and started again. [y/N] ");
            var answer = (await input.ReadLineAsync(cancellationToken))?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("Cancelled.");
                return 0;
            }
        }

        // The operator is the one running this, so there is no boundary to keep — but the rollback package and the
        // log still go in a directory of their own, not the service's (`updates/`, which it can write), so a
        // compromised service cannot plant a package for a rollback to install. Kept if anything failed, so there
        // is a log to read.
        var privateDirectory = Directory.CreateTempSubdirectory("dbdatasync-update-cli-");
        var workspace = new UpdateWorkspace(root, privateDirectory.FullName);
        var applier = new UpdateApplier(new UpdateStateStore(workspace), env.RunnerFor(workspace));
        var request = new UpdateRequest(
            plan.Target.Version.Text, plan.Installed?.Text, plan.Target.Channel,
            plan.Location.Kind, plan.Location.ToolRoot!, plan.SourceDirectory, DateTimeOffset.UtcNow, env.UserName);
        var url = options.Url
            ?? DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:Url")
            ?? "http://localhost:5080";

        var exit = await UpdateApplyFlow.RunAsync(
            request, plan.Service, applier, env.ServiceControl, env.Health, url, options.HealthTimeout, output, error, cancellationToken);
        if (exit == 0)
            privateDirectory.Delete(recursive: true);
        return exit;
    }

    private static void WriteStatus(UpdateStateStore store, TextWriter output)
    {
        output.WriteLine($"Update state ({store.Workspace.Directory})");

        var pending = store.ReadPending();
        if (pending is not null)
            output.WriteLine($"  requested, not yet applied: {pending.TargetVersion} (by {pending.RequestedBy ?? "?"}, {pending.RequestedUtc:yyyy-MM-dd HH:mm} UTC) — applied at the next start of the service");

        var state = store.ReadState();
        if (state.Current is null)
        {
            output.WriteLine("  no update has been attempted here.");
            return;
        }

        if (state.Current.Phase == UpdatePhase.Restarting)
            output.WriteLine("  on trial: installed, and rolls back at the next start unless the new version proves itself");

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
        public string? Url { get; init; }
        public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHealthTimeoutSeconds);
    }
}
