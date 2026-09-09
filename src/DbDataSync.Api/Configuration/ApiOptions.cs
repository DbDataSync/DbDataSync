using DbDataSync.State;
namespace DbDataSync.Api.Configuration;

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
    public const string DefaultStateEngine = StateEngineIds.Sqlite;
    public const int DefaultStatePort = 0;
    public const int DefaultRunRetentionDays = 90;
    public const int DefaultRunRetentionMaxPerMapping = 1_000;
    public const int DefaultChangeCheckRetentionDays = 7;
    public const int DefaultRunPruningIntervalMinutes = 60;

    public required string RepoRoot { get; init; }
    public required string StateDbPath { get; init; }

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

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DbDataSync");

        var repoRoot = section["RepoRoot"] ?? Path.Combine(Directory.GetCurrentDirectory(), "dbdatasync-repo");
        var stateDbPath = section["StateDbPath"] ?? Path.Combine(repoRoot, "state.db");
        var taskRunnerDllPath = section["TaskRunnerDllPath"] ?? ResolveDefaultTaskRunnerDllPath();

        return new ApiOptions
        {
            RepoRoot = repoRoot,
            StateDbPath = stateDbPath,
            // Not validated here — phase 109f moved that to StateDialect.For, the one place that
            // actually needs an answer (StateDatabase.FromOptions, downstream of this). An id this
            // build has never heard of is exactly as fatal as a real deployment needs it to be: with
            // an open id space (a custom StateDialect can be registered), a typo and "I meant a real
            // custom engine that just isn't registered yet" look identical from here, and silently
            // falling back to SQLite would start an empty store instead of surfacing either mistake.
            StateEngine = section["StateEngine"] ?? DefaultStateEngine,
            StateConnectionString = section["StateConnectionString"],
            TaskRunnerDllPath = taskRunnerDllPath,
            StatePort = int.TryParse(section["StatePort"], out var statePort) ? statePort : DefaultStatePort,
            // Defaults applied when unset, rather than "unset means no limit". An operator who wants
            // no limit says so with 0, which is a decision; silence is not.
            RunRetentionDays = ReadCap(section["RunRetentionDays"], DefaultRunRetentionDays),
            RunRetentionMaxPerMapping = ReadCap(section["RunRetentionMaxPerMapping"], DefaultRunRetentionMaxPerMapping),
            ChangeCheckRetentionDays = ReadCap(section["ChangeCheckRetentionDays"], DefaultChangeCheckRetentionDays),
            RunPruningInterval = TimeSpan.FromMinutes(
                int.TryParse(section["RunPruningIntervalMinutes"], out var minutes) && minutes > 0
                    ? minutes
                    : DefaultRunPruningIntervalMinutes),
        };
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
}
