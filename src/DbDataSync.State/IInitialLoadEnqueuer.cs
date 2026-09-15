namespace DbDataSync.State;

/// <summary>
/// The segment-expansion/enqueue core behind "start a bulk load for this mapping" —
/// <c>DbDataSync.Api.Services.BulkLoadService</c>'s own implementation, reached through this interface
/// rather than a direct reference because <see cref="LocalRunnerState"/> lives in this project and
/// <c>BulkLoadService</c> — which needs <c>ConfigRepository</c>, driver connections, a process
/// supervisor, and script-backed segment expansion — does not belong down here, and this project is
/// not allowed to reference <c>DbDataSync.Api</c> without creating a cycle (Api already references
/// this project). Registered as <c>services.AddSingleton&lt;IInitialLoadEnqueuer&gt;(sp =>
/// sp.GetRequiredService&lt;BulkLoadService&gt;())</c> in <c>DbDataSyncHost</c>, the same pattern
/// <c>IConnectionFactory</c>/<c>DriverConnectionFactory</c> already uses.
/// <para>
/// See phase 134's <c>IRunnerState.RequestInitialLoad</c> — <see cref="LocalRunnerState"/>'s
/// implementation of it is this interface's only caller, and takes it as a <see cref="Lazy{T}"/> rather
/// than resolving it directly: this interface's only real implementation (<c>BulkLoadService</c>)
/// depends, transitively, on <c>StateHost</c> — the same <c>IHostedService</c> that depends on
/// <see cref="LocalRunnerState"/> to exist at all — so resolving it eagerly in that constructor closes a
/// DI cycle that hangs the host at startup. See <c>LocalRunnerState</c>'s own doc on the field for the
/// full chain.
/// </para>
/// </summary>
public interface IInitialLoadEnqueuer
{
    /// <summary>
    /// Segments this mapping's own <c>TableMappingConfig.DefaultSegmenting</c> (empty means Full — the
    /// same convention every other scheduled reload path already follows) and enqueues one
    /// <c>RunKind.BulkLoad</c> work item per segment under <paramref name="batchId"/>, which the caller
    /// has already minted and written a pending watermark row under.
    /// </summary>
    Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken);
}
