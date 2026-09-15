using DbDataSync.State;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// A stub <see cref="IInitialLoadEnqueuer"/> for every <see cref="LocalRunnerState"/> these tests
/// construct that has no reason to exercise <c>RequestInitialLoad</c> — phase 134's runner-triggered
/// path is covered by <c>DbDataSync.State.Tests</c> and by <c>RunExecutorTests</c>' own dedicated
/// cases, so every other fixture here just needs the constructor to be satisfiable.
/// </summary>
internal sealed class NeverCalledInitialLoadEnqueuer : IInitialLoadEnqueuer
{
    public Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "This test's LocalRunnerState was not expected to start a Bulk Load.");
}
