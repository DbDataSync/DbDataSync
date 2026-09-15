using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// A real, test-local <see cref="IInitialLoadEnqueuer"/> — the only implementation anywhere in the test
/// suite that actually enqueues processable work, rather than throwing (<see cref="NeverCalledInitialLoadEnqueuer"/>)
/// or merely recording the call. The one real implementation, <c>DbDataSync.Api.Services.BulkLoadService</c>,
/// lives in <c>DbDataSync.Api</c>, which this project does not (and should not) reference — phase 134's
/// own <c>IInitialLoadEnqueuer</c> seam exists specifically so <c>DbDataSync.State</c> doesn't need to.
/// A test that wants a mapping's first pass to actually run its Bulk Load (rather than assert it never
/// tries to) needs its own copy of the same minimal core: mint a <c>BulkLoadBatches</c> row and one
/// <c>WorkQueue</c> row per segment, so the very same worker call already draining the Primary lane
/// picks the Bulk Load segment(s) up on its own lane and actually moves the rows.
/// <para>
/// Deliberately narrower than <c>BulkLoadService.EnqueueForInitialLoadAsync</c>: no row-count estimate
/// (it needs a real driver connection open, and is cosmetic — <c>BulkLoadBatchStore.CreateBatch</c>
/// accepts a null estimate exactly for callers with nothing to offer), and no
/// <c>ProcessSupervisor.EnsureWorkerRunning</c> nudge (nothing to nudge — the test itself already
/// called <c>ExecuteWorkerAsync</c>, so a worker is not merely running, it is the one thing driving this
/// whole call in the first place).
/// </para>
/// </summary>
internal sealed class RealInitialLoadEnqueuer(
    ConfigRepository configRepository, WorkQueueStore workQueueStore, BulkLoadBatchStore batchStore)
    : IInitialLoadEnqueuer
{
    public Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken)
    {
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        // Empty means Full — the same convention BulkLoadService.EnqueueForInitialLoadAsync uses for a
        // runner-triggered load, matching a standalone reload's own DefaultSegmenting convention.
        IReadOnlyList<BatchReloadSegment> segments = mapping.DefaultSegmenting.Count == 0
            ? [new FullSegment()]
            : mapping.DefaultSegmenting;

        batchStore.CreateBatch(batchId, replicationName, mappingName, segments.Count, estimatedRows: null, estimateCaveat: null);

        // Phase 143: EnqueueOrThrow, not Enqueue — this request is not the same thing as whatever else
        // might already be enqueuing this mapping's segments (an explicit Bulk Load trigger racing this
        // mapping's own auto-triggered load, say), so a collision must not be silently attached to it.
        // Mirrors BulkLoadService.EnqueueForInitialLoadAsync's own use of the same throwing variant.
        foreach (var segment in segments)
        {
            workQueueStore.EnqueueOrThrow(
                replicationName, RunKind.BulkLoad, mappingName, segment.Describe(),
                SegmentSerializer.Serialize(segment), WorkItemKinds.FromConfig, batchId);
        }

        return Task.CompletedTask;
    }
}
