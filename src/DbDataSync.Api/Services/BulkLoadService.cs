using DbDataSync.Api.Models;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns a bulk load request into queued work: expand any <see cref="AutoSegment"/> against the real
/// source table, then enqueue one independently-scheduled unit of work per resulting segment.
/// <para>
/// Nothing spawns a process on this request's critical path. Enqueueing is a handful of SQLite writes,
/// which is what makes "queue hundreds of segments at once" a reasonable thing to do — the worker is
/// merely nudged into existence afterwards, and that's a no-op if one is already draining.
/// </para>
/// <para>
/// Also implements <see cref="IInitialLoadEnqueuer"/> — phase 134's runner-triggered path
/// (<see cref="EnqueueForInitialLoadAsync"/>) reuses this same segment-expansion/enqueue core, reached
/// by <c>LocalRunnerState</c> (which lives in <c>DbDataSync.State</c>, and so cannot reference this
/// class directly) through that interface. Registered in <c>DbDataSyncHost</c> as
/// <c>services.AddSingleton&lt;IInitialLoadEnqueuer&gt;(sp => sp.GetRequiredService&lt;BulkLoadService&gt;())</c>.
/// </para>
/// </summary>
public sealed class BulkLoadService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    WorkQueueStore workQueueStore,
    BulkLoadBatchStore batchStore,
    TaskRunStore taskRunStore,
    ProcessSupervisor supervisor,
    CustomSegmentExpansion customSegments) : IInitialLoadEnqueuer
{
    public async Task<TriggerResult> EnqueueAsync(
        string replicationName, string mappingName, BulkLoadRequest request, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        if (request.Segments.Count == 0)
            return TriggerResult.Invalid("At least one segment is required — use a Full segment to reload the whole table.");

        if (mapping.Sources.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} sources; bulk load supports 1:1 mappings in v1.");

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, request.Segments, request.ReaderKind, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            // A bad segment column, an undividable column type, or an unreachable source are all the
            // request being wrong or unrunnable — not a server fault, and worth saying plainly rather
            // than enqueueing work that is guaranteed to fail once a worker claims it.
            return TriggerResult.Invalid(ex.Message);
        }

        var kinds = new WorkItemKinds(request.ReaderKind, request.CacheKind, request.WriterKind);

        // One id for the whole reload, minted here and carried onto every segment's run, so the
        // Monitoring screen can add the segments back up.
        var batchId = Guid.NewGuid().ToString("N");
        var runIds = await CreateBatchAndEnqueueAsync(
            task, mapping, replicationName, mappingName, batchId, segments, kinds,
            throwOnCollision: false, cancellationToken);

        var ensureResult = supervisor.EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>
    /// Phase 134's runner-triggered path: <c>RunExecutor</c> resolved a <c>Primary</c> pass's intent to
    /// <see cref="ReadIntent.InitialLoad"/> against a reader that can capture its position without
    /// reading a row, and <c>IRunnerState.RequestInitialLoad</c> has already made that captured
    /// position durable (<c>ChangeWatermarks.PendingWatermark</c>) and set the mapping's
    /// <see cref="ReadHold.Loading"/> hold before this is ever called — this only ever creates the work
    /// a Primary pass itself cannot run directly.
    /// <para>
    /// **No operator-provided segment list here** — there is no operator in this path to ask for one.
    /// Segmented exactly like an ordinary scheduled reload of this mapping: its own
    /// <see cref="TableMappingConfig.DefaultSegmenting"/>, empty meaning Full, the same convention
    /// <see cref="ReconcileService.EnqueueScheduledAsync"/> already follows for the same reason.
    /// </para>
    /// <para>
    /// <paramref name="batchId"/> is minted by the caller, not here — the caller (<c>LocalRunnerState</c>)
    /// has to write the pending watermark row under this exact id *before* calling this, so a crash
    /// between the two leaves "held, nothing queued yet" rather than "queued, nothing blocking a
    /// concurrent Primary pass".
    /// </para>
    /// </summary>
    public async Task EnqueueForInitialLoadAsync(
        string replicationName, string mappingName, string batchId, CancellationToken cancellationToken)
    {
        var task = configRepository.LoadReplicationTask(replicationName);
        var mapping = configRepository.LoadTableMapping(replicationName, mappingName);

        // Empty means Full — the same convention DefaultSegmenting already has for a standalone
        // reload's own configured segments.
        IReadOnlyList<BatchReloadSegment> requested = mapping.DefaultSegmenting.Count == 0
            ? [new FullSegment()]
            : mapping.DefaultSegmenting;

        var segments = await ExpandAsync(task, mapping, requested, readerKindOverride: null, cancellationToken);

        // Phase 143: unlike the operator-facing path above, this request is not the same thing as
        // whatever else might already be enqueuing this mapping's segments — a losing collision here
        // must not be silently attached to it (see WorkQueueCollisionException's own doc). The caller,
        // LocalRunnerState.RequestInitialLoad, lets this propagate rather than catching it.
        await CreateBatchAndEnqueueAsync(
            task, mapping, replicationName, mappingName, batchId, segments, WorkItemKinds.FromConfig,
            throwOnCollision: true, cancellationToken);

        supervisor.EnsureWorkerRunning(replicationName);
    }

    /// <summary>
    /// The reusable core behind both enqueue paths above: one catalog-statistics row estimate, one
    /// <c>BulkLoadBatches</c> row recording the planned segment count (phase 107 — deliberately not
    /// <c>COUNT(RunId)</c>, which undercounts when an equivalent segment was already in flight), and one
    /// <c>WorkQueue</c> row per segment.
    /// </summary>
    /// <param name="throwOnCollision">True for the auto-triggered-initial-load path, where a collision
    /// with other in-flight work for the same mapping+segment is a different caller's request, not this
    /// one's own — see <see cref="WorkQueueStore.EnqueueOrThrow"/>. False for the operator-facing path,
    /// where two requests for the same segment really are the same thing and should collapse.</param>
    private async Task<IReadOnlyList<Guid>> CreateBatchAndEnqueueAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, string replicationName, string mappingName,
        string batchId, IReadOnlyList<BatchReloadSegment> segments, WorkItemKinds kinds,
        bool throwOnCollision, CancellationToken cancellationToken)
    {
        var (estimatedRows, estimateCaveat) = await EstimateRowsAsync(task, mapping, cancellationToken);
        batchStore.CreateBatch(batchId, replicationName, mappingName, segments.Count, estimatedRows, estimateCaveat);

        // Enqueued one at a time, not via a single LINQ projection, so a later segment's collision can
        // be rolled back against the earlier ones this same call already committed — see the follow-up
        // doc this closes: a multi-segment DefaultSegmenting mapping racing an operator's own reload of
        // the identical scheme used to leave a mix of real and orphaned segments under one batch that
        // could never reach BulkLoadState.Completed, since nothing would ever enqueue the segment that
        // lost. Only relevant when throwOnCollision is true — the operator-facing path's own collisions
        // collapse instead of throwing, so there is nothing here to roll back for it.
        var runIds = new List<Guid>();
        try
        {
            foreach (var segment in segments)
            {
                var runId = throwOnCollision
                    ? workQueueStore.EnqueueOrThrow(
                        replicationName, RunKind.BulkLoad, mappingName, segment.Describe(),
                        SegmentSerializer.Serialize(segment), kinds, batchId)
                    : workQueueStore.Enqueue(
                        replicationName, RunKind.BulkLoad, mappingName, segment.Describe(),
                        SegmentSerializer.Serialize(segment), kinds, batchId);
                runIds.Add(runId);
            }
        }
        catch (WorkQueueCollisionException)
        {
            // Whatever this call already enqueued is real, durable work that nobody else will ever
            // finish on this batch's behalf — cancelled the same way an operator's own CancelRun does
            // (TryCancelPending, then CompleteRun records the terminal status), not merely left Pending,
            // so nothing here reports Queued forever either. The batch row itself is removed rather than
            // left recording a SegmentCount none of its segments can still reach — matching the single-
            // segment case, which leaves no trace of a losing attempt at all.
            foreach (var runId in runIds)
                if (workQueueStore.TryCancelPending(runId))
                    taskRunStore.CompleteRun(runId, RunStatus.Cancelled, 0, 0,
                        "Cancelled: a later segment in the same initial load lost its WorkQueue race.");
            batchStore.DeleteBatch(batchId);
            throw;
        }

        return runIds;
    }

    /// <summary>
    /// A whole-table row count from the source engine's catalog statistics (<c>sys.partitions</c>,
    /// <c>pg_class.reltuples</c>) — a best-effort denominator, not a fact a decision hangs on, so any
    /// failure here just yields <c>(null, null)</c> and the card shows "Unknown".
    /// <para>
    /// Deliberately the whole table even when the mapping has a row <see cref="SourceTableSpec.Filter"/>:
    /// catalog stats can't answer a predicate, and a scaled guess would be worse than an honest
    /// overcount with the caveat attached. Null table (a query source) or a driver with no catalog
    /// (ODBC) → no estimate.
    /// </para>
    /// </summary>
    private async Task<(long? Rows, string? Caveat)> EstimateRowsAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        if (string.IsNullOrWhiteSpace(source.Table))
            return (null, null);

        try
        {
            var (connection, driver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
            await using (connection)
            {
                if (driver is not ITableRowEstimator estimator)
                    return (null, null);

                var rows = await estimator.EstimateRowCountAsync(connection, source, cancellationToken);
                var caveat = rows is not null && !string.IsNullOrWhiteSpace(source.Filter)
                    ? "ignores row filter"
                    : null;
                return (rows, caveat);
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Resolves <see cref="AutoSegment"/>s into concrete ranges. Done here, once, rather than in the
    /// worker: the bounds come from the source's real MIN/MAX, and each resulting range has to become
    /// its own queue row so segments can be scheduled, retried and observed independently.
    /// <para>
    /// Takes a plain segment list rather than a <see cref="BulkLoadRequest"/> so <see cref="EnqueueForInitialLoadAsync"/>
    /// can share it too — that path has no operator-provided request, only the mapping's own
    /// <see cref="TableMappingConfig.DefaultSegmenting"/>. <paramref name="readerKindOverride"/> is the
    /// request's own optional override where there is a request, and null (resolve the mapping's
    /// configured Bulk Load reader) where there is none.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested,
        string? readerKindOverride, CancellationToken cancellationToken)
    {
        var needsAuto = requested.OfType<AutoSegment>().Any();
        var needsCustom = requested.OfType<CustomSegment>().Any();
        if (!needsAuto && !needsCustom)
            return requested;

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var readerKind = PipelineResolution.BulkLoadReaderKind(readerKindOverride, task, mapping);

        var (connection, driver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        await using (connection)
        {
            IReadOnlyList<BatchReloadSegment> segments = requested;

            if (needsCustom)
            {
                // The target only when something might read it — a strategy that segments from a
                // control table needs the connection, and one that generates months from the calendar
                // should not make anyone open one.
                var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
                var (targetConnection, _) = await connections.OpenAsync(target.ConnectionName, cancellationToken);
                await using (targetConnection)
                {
                    segments = await customSegments.ExpandAsync(
                        task, mapping, segments,
                        new SegmentingConnections(connection, targetConnection), cancellationToken);
                }
            }

            if (!segments.OfType<AutoSegment>().Any())
                return segments;

            var reader = driver.Readers.FirstOrDefault(r => r.Kind == readerKind)
                ?? throw new InvalidOperationException($"Source driver does not support reader kind '{readerKind}'.");

            if (reader is not ISegmentExpandingReader expanding)
                throw new InvalidOperationException(
                    $"Reader '{readerKind}' cannot divide a column into buckets, so an Auto segment can't be used with " +
                    "it. Pick a reader that supports segmentation, or specify explicit list/range segments.");

            return await expanding.ExpandAutoSegmentsAsync(
                connection, source, segments, mapping.SourceColumns, mapping.Name, cancellationToken);
        }
    }
}
