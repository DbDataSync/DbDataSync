using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.MsSql;
using DataSync.State;
using DataSync.TaskRunner;

if (!TaskRunnerOptions.TryParse(args, out var options, out var parseError))
{
    Console.Error.WriteLine($"Argument error: {parseError}");
    Console.Error.WriteLine(
        "Usage: DataSync.TaskRunner --repo-root <path> --state-db <path> --replication <name> [--degree-of-parallelism <n>]");
    return (int)ExitCode.ConfigError;
}

var driverRegistry = new DriverRegistry();
driverRegistry.Register(new MsSqlDriver());

var secretStore = new SecretStore(true);
var configRepository = new ConfigRepository(options!.ConfigRoot, new GitCommitService(options.RepoRoot), secretStore);
var stateDatabase = new StateDatabase(options.StateDbPath);

using var logWriter = new LogWriter(stateDatabase);
var executor = new RunExecutor(
    configRepository,
    driverRegistry,
    secretStore,
    new TaskRunStore(stateDatabase),
    new ChangeWatermarkStore(stateDatabase),
    new RunLockStore(stateDatabase),
    new WorkQueueStore(stateDatabase),
    logWriter);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var exitCode = await executor.ExecuteWorkerAsync(options.Replication, options.DegreeOfParallelism, cts.Token);
return (int)exitCode;
