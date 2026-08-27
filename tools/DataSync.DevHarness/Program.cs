using System.Runtime.InteropServices;
using DataSync.DevHarness;

// A development harness for standing up a working DataSync environment and putting real traffic
// through it. Not part of the shipped product — see architecture/implementation/done/phase-011-dev-harness.md.

using var cancellation = new CancellationTokenSource();

// PosixSignalRegistration rather than Console.CancelKeyPress. `up` owns child processes, so failing
// to intercept a stop signal doesn't just end this process — it orphans a running API, a Vite server
// and any TaskRunner workers, holding ports and the scratch state database until they're hunted down
// by hand. CancelKeyPress is not enough for that job on two counts: it depends on console
// initialisation, so it does not reliably fire when output is redirected and there's no terminal
// (a CI job, a `&`-backgrounded run), and it does not cover SIGTERM at all — which is what
// `docker stop`, a CI cancellation and most IDE stop buttons actually send.
void RequestShutdown(PosixSignalContext context)
{
    context.Cancel = true;   // don't let the runtime terminate us before children are taken down
    Log.Info("shutting down…");
    // ReSharper disable once AccessToDisposedClosure — registrations are disposed before the CTS.
    cancellation.Cancel();
}

var signals = new List<PosixSignalRegistration>();
foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
{
    try
    {
        signals.Add(PosixSignalRegistration.Create(signal, RequestShutdown));
    }
    catch (PlatformNotSupportedException)
    {
        // Not every signal is deliverable on every platform (SIGQUIT on Windows); the ones that are
        // still get us a clean shutdown.
    }
}

if (!HarnessArgs.TryParse(args, out var parsed, out var parseError))
{
    if (args.Length > 0 && args[0] is not ("help" or "--help" or "-h"))
        Log.Error(parseError!);
    PrintUsage();
    return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 2;
}

var harness = parsed!;
var repoRoot = FindRepoRoot();

try
{
    switch (harness.Verb.ToLowerInvariant())
    {
        case "help":
            PrintUsage();
            return 0;

        case "up":
            await UpAsync(harness, repoRoot, cancellation.Token);
            return 0;

        case "down":
            DockerCompose.Down(repoRoot, removeVolumes: harness.Has("volumes"));
            return 0;

        case "reset":
            await SqlBootstrap.CreateAsync(TargetEngine.Resolve(harness), cancellation.Token);
            AppProcesses.ResetScratchRepo();
            Log.Ok("databases recreated and the scratch config repo cleared");
            Log.Info("Run `up` again to reconfigure the replication.");
            return 0;

        case "seed":
            await SqlBootstrap.SeedAsync(harness.Int("rows", 1000), cancellation.Token);
            return 0;

        case "workload":
            await Workload.RunAsync(
                harness.Int("rate", 10),
                harness.Duration("duration", TimeSpan.FromMinutes(1)),
                cancellation.Token);
            return 0;

        case "drift":
            await Drift.InjectAsync(TargetEngine.Resolve(harness), harness.Int("rows", 30), cancellation.Token);
            return 0;

        case "verify":
            return await Verifier.VerifyAsync(TargetEngine.Resolve(harness), cancellation.Token) ? 0 : 1;

        default:
            Log.Error($"Unknown verb '{harness.Verb}'.");
            PrintUsage();
            return 2;
    }
}
catch (HarnessException ex)
{
    Log.Error(ex.Message);
    return 1;
}
catch (Microsoft.Data.SqlClient.SqlException ex)
{
    // A database error here is an operational fact about the environment, not a defect in the
    // harness — report it the way every other failure is reported rather than as a stack dump.
    Log.Error($"SQL Server error {ex.Number}: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    return 0;
}
finally
{
    foreach (var registration in signals)
        registration.Dispose();
}

async Task UpAsync(HarnessArgs options, string root, CancellationToken cancellationToken)
{
    var apiPort = options.Int("api-port", 5183);
    var spaPort = options.Int("spa-port", 5173);
    var apiUrl = options.String("api-url", $"http://127.0.0.1:{apiPort}")!;
    var startApp = !options.Has("no-app");

    if (!options.Has("no-containers"))
    {
        DockerCompose.Up(root);
        await DockerCompose.WaitForServersAsync(TimeSpan.FromMinutes(3), cancellationToken);
    }

    var targetEngine = TargetEngine.Resolve(options);

    if (!options.Has("keep-data"))
        await SqlBootstrap.CreateAsync(targetEngine, cancellationToken);

    var seedRows = options.Int("rows", 200);
    if (seedRows > 0 && !options.Has("keep-data"))
        await SqlBootstrap.SeedAsync(seedRows, cancellationToken);

    await using var app = new AppProcesses();
    if (startApp)
    {
        AppProcesses.ResetScratchRepo();
        app.StartApi(root, apiPort);
        app.StartSpa(root, spaPort, apiPort);
    }
    else
    {
        Log.Info($"--no-app: expecting an API already listening at {apiUrl}");
    }

    using var api = new ApiClient(apiUrl);
    await api.WaitUntilHealthyAsync(TimeSpan.FromMinutes(1), cancellationToken);
    await api.ConfigureScenarioAsync(targetEngine, cancellationToken);

    if (!startApp)
    {
        Log.Ok("environment is ready");
        return;
    }

    Console.WriteLine();
    Log.Ok($"Ready — open http://127.0.0.1:{spaPort} and pick the '{Scenario.ReplicationName}' replication.");
    Log.Info($"Config repo and state database: {AppProcesses.ScratchRepoRoot}");
    Log.Info("In another terminal, try:");
    Log.Info("  tools/dev-harness workload --rate 20 --duration 2m   # live inserts/updates/deletes");
    Log.Info("  tools/dev-harness verify                             # source vs target, row by row");
    Log.Info("  tools/dev-harness drift                              # corrupt the target, then backfill in the UI");
    Log.Info("Ctrl+C stops the API and SPA (the containers keep running — `down` stops those).");
    Console.WriteLine();

    var reason = await app.WaitForAnyExitAsync(cancellationToken);
    Log.Warn(reason ?? "a child process exited");
}

// The harness runs `docker compose` and locates build output relative to the repository root, so it
// has to work from any working directory — walking up to the solution file is how every other tool in
// this repo that needs the same thing (see TestApiFactory) does it.
static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.slnx")))
        directory = directory.Parent;

    return directory?.FullName
        ?? throw new HarnessException("Could not locate the repository root (no DataSync.slnx in any parent directory).");
}

static void PrintUsage()
{
    Console.WriteLine("""
        DataSync dev harness — stand up a working environment and put real traffic through it.

        Usage: tools/dev-harness <verb> [options]

          up          Start containers, create and seed the databases, start the API and SPA, and
                      configure the replication. Blocks until Ctrl+C.
                      --rows N          rows to seed        (default 200, 0 to skip)
                      --api-port N      API port            (default 5183)
                      --spa-port N      SPA port            (default 5173)
                      --no-app          don't start the API/SPA; configure an already-running one
                      --api-url URL     where that API is   (default http://127.0.0.1:<api-port>)
                      --no-containers   assume the containers are already up
                      --keep-data       don't recreate or reseed the databases
                      --target-engine E replicate into mssql (default) or postgres

          down        Stop the containers.
                      --volumes         also discard their data volumes

          reset       Recreate the databases and clear the scratch config repo, leaving containers up.
                      --target-engine E as for `up`

          seed        Bulk-load rows into the source.
                      --rows N          (default 1000)

          workload    Run continuous insert/update/delete transactions against the source.
                      --rate N          transactions per second (default 10)
                      --duration T      e.g. 90s, 5m, 1h        (default 1m)

          drift       Corrupt the target directly, behind the replication's back — the situation only
                      a reconciling backfill can repair.
                      --rows N          rows to affect, split across delete/alter/phantom (default 30)
                      --target-engine E as for `up`

          verify      Compare source and target row by row. Exit code 0 if identical, 1 if not.
                      Works across engines: a SQL Server source against a PostgreSQL target.
                      --target-engine E as for `up`

        Cross-engine:
          The source is always SQL Server — Change Tracking is what makes the incremental story
          demonstrable. With --target-engine postgres the replication runs BatchReload/StagingTable/
          DeleteInsert instead, because a non-SQL-Server target has no upsert writer yet, so it
          reloads rather than applying changes. Set DATASYNC_HARNESS_TARGET_ENGINE once instead of
          passing the flag to every verb — `verify` and `drift` must agree with what `up` configured.

        Environment:
          DATASYNC_MSSQL_SA_PASSWORD      SA password for both SQL Server instances (default DataSync_Test_Pw1)
          DATASYNC_POSTGRES_PASSWORD      PostgreSQL password (default DataSync_Test_Pw1)
          DATASYNC_HARNESS_TARGET_ENGINE  mssql (default) or postgres
        """);
}
