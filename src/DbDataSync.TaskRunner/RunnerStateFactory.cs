using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.State.Remote;

namespace DbDataSync.TaskRunner;

/// <summary>
/// Builds the two owner-facing surfaces this runner will use — state, and the one config write a run
/// can make. There is exactly one route to each: over loopback, to the process that owns them.
/// <para>
/// There is deliberately no local fallback. A runner started without an endpoint would open the state
/// file itself, and a runner started by hand while the API is up would then be a second writer to it —
/// which is the thing phase 39 exists to make impossible. Refusing to start is the correct behaviour,
/// and it fails at startup with a message rather than later with corruption.
/// </para>
/// <para>
/// The runner still knows <c>StateDbPath</c> — its journal is written beside the state file, and the
/// script cache lives next to it — but it never opens it.
/// </para>
/// </summary>
internal static class RunnerStateFactory
{
    public static (IRunnerState State, IRunnerConfig Config, IDisposable Scope) Create(TaskRunnerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StateEndpoint))
        {
            throw new InvalidOperationException(
                $"No state endpoint was supplied. Set {StateProtocol.EndpointEnvironmentVariable} or pass " +
                "--state-endpoint. A TaskRunner never opens the state file directly: the API owns it, and " +
                "a second writer is what this refusal prevents.");
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

        // The same client, so the same endpoint, token and timeout: what a child writes is a different
        // question from how it reaches its parent. No journal of its own, and no grace period —
        // RemoteRunnerConfig explains why a provisioning report needs neither.
        var config = new RemoteRunnerConfig(http, Console.Error.WriteLine);

        return (remote, config, new Scope(() => { remote.Dispose(); http.Dispose(); }));
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
