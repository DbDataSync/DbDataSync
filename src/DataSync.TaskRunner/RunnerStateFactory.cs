using DataSync.State;
using DataSync.State.Remote;

namespace DataSync.TaskRunner;

/// <summary>
/// Builds the state surface this runner will use — remote when the API spawned it, direct when it was
/// started standalone.
/// </summary>
internal static class RunnerStateFactory
{
    public static (IRunnerState State, IDisposable Scope) Create(TaskRunnerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StateEndpoint))
        {
            // Standalone: no owner to talk to, so this process is the owner. Kept for the dev harness
            // and for running a worker by hand; the API never spawns a runner this way.
            var database = new StateDatabase(options.StateDbPath);
            var logs = new LogWriter(database);
            var local = new LocalRunnerState(
                new TaskRunStore(database), new WorkQueueStore(database), new RunLockStore(database),
                new ChangeWatermarkStore(database), logs);
            return (local, new Scope(logs.Dispose));
        }

        // The token arrives by environment, never as an argument: /proc/<pid>/cmdline is world-readable
        // and /proc/<pid>/environ is not, so an argument would show this secret to every local user
        // through `ps` — which is exactly what it exists to prevent.
        var token = Environment.GetEnvironmentVariable(StateProtocol.TokenEnvironmentVariable);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                $"A state endpoint was supplied but {StateProtocol.TokenEnvironmentVariable} was not. " +
                "The token is passed through the environment deliberately; it must not be an argument.");
        }

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.StateEndpoint!),
            // Short: this is loopback. A long timeout here would delay noticing the owner is gone by
            // exactly that much, on every call, for the whole grace period.
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add(StateProtocol.TokenHeader, token);

        var journal = new StateJournal(
            StateJournal.PathFor(options.StateDbPath, options.Replication, Guid.CreateVersion7()));

        var remote = new RemoteRunnerState(
            http, journal, TimeSpan.FromSeconds(options.StateGraceSeconds), Console.Error.WriteLine);

        return (remote, new Scope(() => { remote.Dispose(); http.Dispose(); }));
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
