using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using DbDataSync.Providers;
using DbDataSync.Scripting;
using DbDataSync.State;
using DbDataSync.State.Remote;
using DbDataSync.TaskRunner;

if (!TaskRunnerOptions.TryParse(args, out var options, out var parseError))
{
    Console.Error.WriteLine($"Argument error: {parseError}");
    Console.Error.WriteLine(
        "Usage: DbDataSync.TaskRunner --repo-root <path> --state-db <path> --replication <name> " +
        "[--degree-of-parallelism <n>] [--backfill-parallelism <n>]");
    return (int)ExitCode.ConfigError;
}

var secretStore = new SecretStore("DbDataSync", true);
var configRepository = new ConfigRepository(options!.ConfigRoot, new GitCommitService(options.RepoRoot), secretStore);

// The cache is what makes scripting affordable here: this process is spawned per run, so without it
// every pass would start a compiler before compiling anything of ours.
var scriptHost = new ScriptHost(
    configRepository, new ScriptCompiler(ScriptCacheDirectory.BesideStateDatabase(options.StateDbPath)));

// Loaded before DriverRegistry for the same reason the API loads it first: a descriptor or compiled
// driver registered from 109d/109e on resolves its provider through this. The worker is a separate
// process per phase 24's "two composition roots" note, so it repeats this rather than sharing the
// API's in-memory registry.
new ProviderRegistry(options.RepoRoot).LoadAll();

var driverRegistry = new DriverRegistry();
// The scripted reader is composed here rather than inside a driver, because it needs the script host
// and a driver must not depend on Roslyn.
driverRegistry.RegisterWithScripting(new MsSqlDriver(), scriptHost);
driverRegistry.RegisterWithScripting(new PostgresDriver(), scriptHost);
driverRegistry.RegisterWithScripting(new DuckDbDriver(), scriptHost);

// Phase 39: a runner spawned by the API never opens the state file. It applies its changes over
// loopback to the process that owns it, and journals to disk if that process goes away mid-run.
// Phase 94: the same is true of the one config write a run makes — the target shape its own
// provisioning produced. It reports it; the API commits it.
var (state, runnerConfig, disposeState) = RunnerStateFactory.Create(options);
using var _stateScope = disposeState;
var executor = new RunExecutor(
    configRepository, driverRegistry, secretStore, state, runnerConfig, scriptHost, options.StateDbPath);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

ExitCode exitCode;
try
{
    exitCode = await executor.ExecuteWorkerAsync(options.Replication, options.Lanes, cts.Token);
}
catch (StateOwnerUnavailableException ex)
{
    // Not a failure of the work — the owner went away. Whatever this run had already achieved is in a
    // journal beside the state database, and the owner applies it before scheduling anything new for
    // this replication.
    Console.Error.WriteLine($"State owner unavailable: {ex.Message}");
    exitCode = ExitCode.StateOwnerUnavailable;
}

// Flush before the process ends: buffered log lines either reach the owner or reach the journal, and
// silently dropping them is the one outcome that leaves an operator with nothing.
if (state is RemoteRunnerState remote)
{
    remote.Flush();
    if (remote.OwnerLost)
        exitCode = ExitCode.StateOwnerUnavailable;
}

return (int)exitCode;
