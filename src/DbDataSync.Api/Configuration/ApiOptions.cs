using DbDataSync.Api.Auth;
using DbDataSync.State;
using DbDataSync.Updates;
namespace DbDataSync.Api.Configuration;

/// <summary>Phase 164: <c>Updates:Mode</c> — replaces the old bare <c>SelfUpdateEnabled</c> boolean.
/// Room for a future <c>Auto</c> without another schema change; <c>Manual</c> is today's "on" (an admin
/// triggers it from the console).</summary>
public enum UpdatesMode { Disabled, Manual }

/// <summary>Phase 164: <c>Notes:MarkdownRenderer</c> — replaces the old bare <c>NotesRichMarkdown</c>
/// boolean. <see cref="Basic"/> is the deliberately small, safe-by-having-almost-nothing-in-it renderer;
/// <see cref="Rich"/> is the full one (tables, task lists, strikethrough).</summary>
public enum NotesRenderer { Basic, Rich }

/// <summary>
/// Deployment-specific paths, read from the "DbDataSync" configuration section (appsettings.json,
/// environment variables, etc). RepoRoot/StateDbPath default to a local dev-friendly location so
/// "just run the API" works out of the box; TaskRunnerDllPath defaults to the sibling
/// DbDataSync.TaskRunner build output next to this project's own build output, which only holds for
/// this repo's own dev layout — override it for any real deployment.
/// </summary>
public sealed class ApiOptions
{
    // Named so the admin config screen's "Reset" action (phase 81 follow-up) can offer exactly the
    // literal FromConfiguration falls back to, rather than a second copy of these numbers that could
    // drift from the one actually applied.
    public const string DefaultUrl = "http://localhost:5080";
    public const string DefaultStateEngine = StateEngineIds.Sqlite;
    public const int DefaultStatePort = 0;
    public const int DefaultRunRetentionDays = 90;
    public const int DefaultRunRetentionMaxPerMapping = 1_000;
    public const int DefaultChangeCheckRetentionDays = 7;
    public const int DefaultRunPruningIntervalMinutes = 60;
    public const FeatureMode DefaultNugetSearchMode = FeatureMode.Enabled;
    public const NotesRenderer DefaultNotesRenderer = NotesRenderer.Basic;

    // Phase 159: applying an update from the console replaces the code the service runs, as the service's
    // own account, so every default here is the closed one.
    public const UpdatesMode DefaultSelfUpdateMode = UpdatesMode.Disabled;
    public const string DefaultSelfUpdateChannels = "stable";
    public const int DefaultSelfUpdateDrainTimeoutSeconds = 120;
    public const int DefaultSelfUpdateConfirmAfterSeconds = 60;

    public required string RepoRoot { get; init; }
    public required string StateDbPath { get; init; }

    /// <summary>
    /// The console/API bind address — see <c>ServeCommand</c> for how this actually becomes Kestrel's
    /// <c>--urls</c>; this is a record of what that resolution already decided, not a second resolution
    /// of its own, since <c>DbDataSyncHost.InsertConfigFile</c> wires the same config file/environment/
    /// command-line sources in ahead of this being constructed.
    /// </summary>
    public string Url { get; init; } = DefaultUrl;

    /// <summary>
    /// Every other origin this deployment is also legitimately reached at, beyond <see cref="Url"/> —
    /// purely additive, never a replacement (see <see cref="Auth.PasskeyOptions.Origins"/>, its one
    /// consumer today). Comma- or semicolon-separated in configuration, matching
    /// <see cref="SelfUpdateChannels"/>'s own list shape.
    /// </summary>
    public IReadOnlyList<string> AlternateUrls { get; init; } = [];

    /// <summary>
    /// Which database backs the state store — see phase 63.
    /// <para>
    /// **Two fields rather than one connection string with a provider hint**, resolving the phase
    /// doc's open question. A connection string's shape already differs per provider, so a hint
    /// embedded in it would have to be parsed back out before the string could be handed to anything —
    /// and getting it wrong would surface as a driver-level parse error rather than as "you named an
    /// engine that does not exist". Two fields make the choice explicit and each half validatable on
    /// its own.
    /// </para>
    /// <para>
    /// Chosen once when a deployment is stood up. There is deliberately no cross-engine migration, so
    /// pointing an existing deployment at a different engine starts an empty store rather than moving
    /// anything — a follow-up, per the plan.
    /// </para>
    /// </summary>
    public string StateEngine { get; init; } = StateEngineIds.Sqlite;

    /// <summary>
    /// How to reach that engine. Ignored for SQLite, which uses <see cref="StateDbPath"/> — the
    /// setting an existing deployment already has, and the reason an unconfigured one keeps behaving
    /// exactly as it did.
    /// </summary>
    public string? StateConnectionString { get; init; }
    public required string TaskRunnerDllPath { get; init; }

    /// <summary>Phase 109j: where <c>DbDataSync.Cli.dll</c> is, for
    /// <c>LibraryValidationLauncher</c> to spawn <c>dotnet exec &lt;this&gt; config library validate
    /// ...</c> — the deep, connection-scoped check, isolated in its own child process for the identical
    /// reason <see cref="TaskRunnerDllPath"/> is. Resolved the same way, for the same reason: see
    /// <see cref="ResolveDefaultCliDllPath"/>.</summary>
    public required string CliDllPath { get; init; }

    /// <summary>
    /// The loopback-only port the runner-state endpoint listens on (phase 39). 0 — the default — binds
    /// an ephemeral one.
    /// <para>
    /// Ephemeral by default because nothing ever has to know this port in advance: the only clients are
    /// children this process spawns, and it tells each one the address it actually bound
    /// (<c>StateHost.BaseAddress</c>). A fixed default would buy nothing and cost a collision every
    /// time two instances run on one host — a developer's own API and a test run, say. It is settable
    /// for the one case that wants it: an operator who would rather firewall a known port than trust
    /// the loopback binding.
    /// </para>
    /// </summary>
    public int StatePort { get; init; }

    /// <summary>
    /// How long a finished run's history is kept, in days. Null keeps runs forever.
    /// <para>
    /// **90 days by default, rather than unlimited.** A state database that only ever grows is not a
    /// policy anyone chose; it is what happens when nobody chooses one, and the cost lands months
    /// later on whoever is trying to work out why the API got slow. Ninety days is long enough to
    /// answer "was this mapping always this slow" across a quarter, which is the longest question run
    /// history is actually asked.
    /// </para>
    /// </summary>
    public int? RunRetentionDays { get; init; }

    /// <summary>
    /// How many finished runs are kept per table mapping. Null keeps every run within the age cap.
    /// <para>
    /// **Per mapping, not global**, which is the entire reason this exists alongside the age cap: a
    /// continuous replication of one busy table can produce thousands of runs a day, and a global cap
    /// would let it evict a quiet mapping's entire history — exactly the history somebody goes looking
    /// for when that quiet mapping finally breaks.
    /// </para>
    /// <para>
    /// 1,000 by default: more than any run-history view pages through, and small enough that a mapping
    /// running every fifteen seconds does not carry a year of rows to satisfy a question about the last
    /// few days.
    /// </para>
    /// </summary>
    public int? RunRetentionMaxPerMapping { get; init; }

    /// <summary>
    /// How long the scheduler's change-check history is kept, in days — see phase 75. Null keeps it
    /// forever.
    /// <para>
    /// **A second knob, reluctantly.** Phase 75 was asked to reuse <see cref="RunRetentionDays"/>
    /// before adding a setting anyone has to learn, and to say so if reuse produced a table that
    /// outgrew what that window was tuned for. It does, by two orders of magnitude. Run history is
    /// one row per run; this is one row per scheduler tick per source-database group, and the tick is
    /// every five seconds whether or not any replication is due — 17,280 rows per group per day, so
    /// ninety days is roughly 1.5 million rows for a single group and tens of millions for a
    /// deployment with a handful. That is not a retention policy, it is the unbounded growth
    /// <see cref="RunRetentionDays"/> exists to prevent, arriving through a different table.
    /// </para>
    /// <para>
    /// Seven days by default, which is the span the history is actually questioned over: "was the
    /// source quiet overnight, or did the gate stop looking" is asked about last night or last week,
    /// never about last quarter. Set it to 0 to keep everything, the same way the run caps read 0.
    /// </para>
    /// </summary>
    public int? ChangeCheckRetentionDays { get; init; }

    /// <summary>
    /// How often pruning runs. Hourly, and coarse on purpose: nothing about retention is
    /// time-sensitive, the work is a handful of deletes, and a frequent sweep would be contention with
    /// the writers that matter for no benefit anybody could observe.
    /// </summary>
    public TimeSpan RunPruningInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the Libraries screen's search box (phase 119) may call the public NuGet index. Enabled by
    /// default; an air-gapped or locked-down deployment sets this to disabled so the endpoint refuses the
    /// call outright rather than timing out against a network it was never going to reach.
    /// </summary>
    public FeatureMode NugetSearchMode { get; init; } = DefaultNugetSearchMode;

    /// <summary>
    /// Which renderer Notes use. <see cref="NotesRenderer.Basic"/> (the default) is deliberately small — a note is
    /// stored input, written by one operator and rendered in other people's sessions, and the small renderer is safe by
    /// having almost nothing in it. <see cref="NotesRenderer.Rich"/> adds tables, task lists and strikethrough (phase
    /// 161).
    /// </summary>
    public NotesRenderer NotesRenderer { get; init; } = DefaultNotesRenderer;

    /// <summary>
    /// Phase 159/164: whether, and how, an admin may update this installation from the web console.
    /// <see cref="UpdatesMode.Disabled"/> by default — a page that can replace the code a service runs is
    /// a capability an operator turns on deliberately. <see cref="UpdatesMode.Manual"/> is today's "on":
    /// an admin triggers it themselves from the Updates screen.
    /// </summary>
    public UpdatesMode SelfUpdateMode { get; init; } = DefaultSelfUpdateMode;

    /// <summary>Which release channels the console may offer: any of <c>stable</c>, <c>beta</c>,
    /// <c>snapshot</c>. Only <c>stable</c> by default — a beta is a prerelease, and a snapshot is a development
    /// build whose only integrity check is a same-origin checksum.</summary>
    public IReadOnlyList<ReleaseChannel> SelfUpdateChannels { get; init; } = [ReleaseChannel.Stable];

    /// <summary>How long an update waits for running work to finish before it restarts the service anyway.
    /// What is interrupted is reconciled at the next start; this only bounds how polite the wait is.</summary>
    public TimeSpan SelfUpdateDrainTimeout { get; init; } = TimeSpan.FromSeconds(DefaultSelfUpdateDrainTimeoutSeconds);

    /// <summary>How long a freshly updated version must have been serving before it counts as having worked.
    /// Not merely "ready": a version that starts and then dies ten seconds later must still be rolled back.</summary>
    public TimeSpan SelfUpdateConfirmAfter { get; init; } = TimeSpan.FromSeconds(DefaultSelfUpdateConfirmAfterSeconds);

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var app = configuration.GetSection("DbDataSync:App");
        var state = configuration.GetSection("DbDataSync:State");
        var retention = configuration.GetSection("DbDataSync:State:Retention");
        var updates = configuration.GetSection("DbDataSync:Updates");
        var nuget = configuration.GetSection("DbDataSync:Nuget:Search");
        var notes = configuration.GetSection("DbDataSync:Notes");

        var repoRoot = app["RepoRoot"] ?? Path.Combine(Directory.GetCurrentDirectory(), "dbdatasync-repo");
        var stateDbPath = state["DbPath"] ?? Path.Combine(repoRoot, "state.db");
        var taskRunnerDllPath = app["TaskRunnerDllPath"] ?? ResolveDefaultTaskRunnerDllPath();
        var cliDllPath = app["CliDllPath"] ?? ResolveDefaultCliDllPath();

        return new ApiOptions
        {
            RepoRoot = repoRoot,
            Url = string.IsNullOrWhiteSpace(app["Url"]) ? DefaultUrl : app["Url"]!,
            AlternateUrls = ReadList(app["AlternateUrls"]),
            StateDbPath = stateDbPath,
            CliDllPath = cliDllPath,
            // Not validated here — phase 109f moved that to StateDialect.For, the one place that
            // actually needs an answer (StateDatabase.FromOptions, downstream of this). An id this
            // build has never heard of is exactly as fatal as a real deployment needs it to be: with
            // an open id space (a custom StateDialect can be registered), a typo and "I meant a real
            // custom engine that just isn't registered yet" look identical from here, and silently
            // falling back to SQLite would start an empty store instead of surfacing either mistake.
            StateEngine = state["Engine"] ?? DefaultStateEngine,
            StateConnectionString = state["ConnectionString"],
            TaskRunnerDllPath = taskRunnerDllPath,
            StatePort = int.TryParse(state["Port"], out var statePort) ? statePort : DefaultStatePort,
            // Defaults applied when unset, rather than "unset means no limit". An operator who wants
            // no limit says so with 0, which is a decision; silence is not.
            RunRetentionDays = ReadCap(retention["RunDays"], DefaultRunRetentionDays),
            RunRetentionMaxPerMapping = ReadCap(retention["RunMaxPerMapping"], DefaultRunRetentionMaxPerMapping),
            ChangeCheckRetentionDays = ReadCap(retention["ChangeCheckDays"], DefaultChangeCheckRetentionDays),
            RunPruningInterval = TimeSpan.FromMinutes(
                int.TryParse(retention["PruningIntervalMinutes"], out var minutes) && minutes > 0
                    ? minutes
                    : DefaultRunPruningIntervalMinutes),
            NugetSearchMode = ConfigEnum.Parse(nuget["Mode"], DefaultNugetSearchMode),
            NotesRenderer = ConfigEnum.Parse(notes["MarkdownRenderer"], DefaultNotesRenderer),
            SelfUpdateMode = ConfigEnum.Parse(updates["Mode"], DefaultSelfUpdateMode),
            SelfUpdateChannels = ReadChannels(updates["Channels"]),
            SelfUpdateDrainTimeout = TimeSpan.FromSeconds(
                int.TryParse(updates["DrainTimeoutSeconds"], out var drain) && drain >= 0
                    ? drain
                    : DefaultSelfUpdateDrainTimeoutSeconds),
            SelfUpdateConfirmAfter = TimeSpan.FromSeconds(
                int.TryParse(updates["ConfirmAfterSeconds"], out var confirm) && confirm >= 0
                    ? confirm
                    : DefaultSelfUpdateConfirmAfterSeconds),
        };
    }

    /// <summary>Comma- or semicolon-separated, one scalar value rather than a YAML block sequence — the
    /// shape every list-shaped <c>DbDataSync:*</c> setting uses, so it's writable through the same
    /// text-editing config-file writer every scalar setting already is.</summary>
    internal static IReadOnlyList<string> ReadList(string? configured) =>
        (configured ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// A comma- or semicolon-separated list of channel names. Anything that is not a channel is ignored rather
    /// than refused, and an empty result falls back to stable only — a typo must never widen what an update may
    /// install, so the failure mode is the narrowest setting.
    /// </summary>
    internal static IReadOnlyList<ReleaseChannel> ReadChannels(string? configured)
    {
        var channels = (configured ?? DefaultSelfUpdateChannels)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.All(char.IsAsciiLetter) && Enum.TryParse<ReleaseChannel>(name, ignoreCase: true, out _))
            .Select(name => Enum.Parse<ReleaseChannel>(name, ignoreCase: true))
            .Distinct()
            .ToList();
        return channels.Count == 0 ? [ReleaseChannel.Stable] : channels;
    }

    /// <summary>
    /// A retention cap: the configured number, the default when nothing is configured, or null when the
    /// operator explicitly asked for no cap by setting 0.
    /// <para>
    /// Zero as "keep everything" rather than as "keep nothing", because the alternative reading of a
    /// mistyped 0 is a policy that deletes all run history the first time it sweeps. Between two
    /// interpretations of the same typo, the recoverable one wins.
    /// </para>
    /// </summary>
    private static int? ReadCap(string? configured, int defaultValue)
    {
        if (configured is null)
            return defaultValue;

        return int.TryParse(configured, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// Where the worker executable is, in the order the possibilities are actually true.
    /// <para>
    /// **Beside the running assembly first**, because that is what every published layout looks like —
    /// the tool, the container, and a plain <c>dotnet publish</c> all put the two in one directory.
    /// The dev-layout guess comes second and is kept only so this repo's own inner loop is unchanged:
    /// it swaps a <c>DbDataSync.Api/bin</c> segment for <c>DbDataSync.TaskRunner/bin</c>, which is true of
    /// this working tree and of nothing else, and was the only answer before.
    /// </para>
    /// <para>
    /// Not finding it returns the beside-the-assembly path anyway, so the failure names a real path
    /// somebody can look at rather than a plausible-looking one that never existed.
    /// </para>
    /// </summary>
    private static string ResolveDefaultTaskRunnerDllPath()
    {
        const string dll = "DbDataSync.TaskRunner.dll";

        var beside = Path.Combine(AppContext.BaseDirectory, dll);
        if (File.Exists(beside))
            return beside;

        var devLayout = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("DbDataSync.Api", "bin"),
                Path.Combine("DbDataSync.TaskRunner", "bin")),
            dll);

        return File.Exists(devLayout) ? devLayout : beside;
    }

    /// <summary>
    /// Same shape as <see cref="ResolveDefaultTaskRunnerDllPath"/>, for the same reason: **beside the
    /// running assembly first** — the real deployment shape, since production actually runs as `dotnet
    /// /app/DbDataSync.Cli.dll serve ...` (the Dockerfile's own entrypoint), which hosts
    /// `DbDataSyncHost.Build()` *inside that same process* — so `AppContext.BaseDirectory` already *is*
    /// `DbDataSync.Cli.dll`'s own publish output directory, with `DbDataSync.Api.dll` sitting right next
    /// to it (a `dotnet publish` of a project pulls every `ProjectReference`'s output into one flat
    /// directory, and `DbDataSync.Cli.csproj` references `DbDataSync.Api.csproj`). The dev-layout guess
    /// swaps a `DbDataSync.Api/bin` segment for `DbDataSync.Cli/bin` — the one thing that's true of this
    /// working tree's own separate per-project build outputs and nothing else.
    /// </summary>
    private static string ResolveDefaultCliDllPath()
    {
        const string dll = "DbDataSync.Cli.dll";

        var beside = Path.Combine(AppContext.BaseDirectory, dll);
        if (File.Exists(beside))
            return beside;

        var devLayout = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("DbDataSync.Api", "bin"),
                Path.Combine("DbDataSync.Cli", "bin")),
            dll);

        return File.Exists(devLayout) ? devLayout : beside;
    }
}
