using DbDataSync.Api.Models;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Turns an on-demand delete-reconcile request into queued work — phase 124's
/// <c>POST /api/replications/{r}/mappings/{m}/reconcile-deletes</c>. Near-identical to
/// <see cref="BackfillService"/>: expand any <see cref="AutoSegment"/> against the real source table,
/// then enqueue one independently-scheduled <see cref="RunKind.ReconcileDeletes"/> unit of work per
/// resulting segment, always through the <c>KeyReconcile</c>/<c>StagingTable</c>/<c>KeyReconcileDelete</c>
/// pipeline — there is no reader/cache/writer choice to make here, unlike a backfill.
/// </summary>
public sealed class ReconcileService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    WorkQueueStore workQueueStore,
    ProcessSupervisor supervisor,
    CustomSegmentExpansion customSegments)
{
    private const string ReaderKind = "KeyReconcile";
    private const string CacheKind = "StagingTable";
    private const string WriterKind = "KeyReconcileDelete";

    public async Task<TriggerResult> EnqueueAsync(
        string replicationName, string mappingName, ReconcileDeletesRequest request, CancellationToken cancellationToken)
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
            return TriggerResult.Invalid("At least one segment is required — use a Full segment to sweep the whole table.");

        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} source(s) and " +
                $"{mapping.Targets.Count} target(s); reconcile-deletes supports 1:1 mappings.");

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, request.Segments, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            // A bad segment column, an undividable column type, a keyless source, or an unreachable
            // source are all the request being wrong or unrunnable — worth saying plainly rather than
            // enqueueing work guaranteed to fail once a worker claims it.
            return TriggerResult.Invalid(ex.Message);
        }

        var kinds = new WorkItemKinds(ReaderKind, CacheKind, WriterKind);
        // The mapping's own configured guard (phase 125's ReconcileConfig.DeleteGuard, or its default —
        // a fresh ReconcileConfig always has one) unless the operator explicitly overrides it for this
        // request. Always serialized, never left for the writer's own hardcoded fallback to resolve —
        // that fallback exists only for a work item nothing here wrote (there is none any more).
        var guard = request.OverrideGuard ? new NoneDeleteGuard() : PipelineResolution.Reconcile(task, mapping).DeleteGuard;
        var guardJson = DeleteGuardOption.Serialize(guard);

        var runIds = segments
            .Select(segment => workQueueStore.Enqueue(
                replicationName,
                RunKind.ReconcileDeletes,
                mappingName,
                segment.Describe(),
                SegmentSerializer.Serialize(segment),
                kinds,
                backfillBatchId: null,
                deleteGuardJson: guardJson))
            .ToList();

        var ensureResult = supervisor.EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>
    /// Phase 125's scheduled path: <see cref="SchedulerService"/> found this mapping due (by cadence,
    /// by after-change, or both) and calls this instead of an operator's own request. Segmented exactly
    /// like an on-demand sweep, except the segments come from the mapping's own
    /// <see cref="TableMappingConfig.DefaultSegmenting"/> — "segmenting stays on the mapping" is the
    /// same rule a standalone reload replication's own configured segments already follow — and the
    /// guard comes from the resolved <see cref="ReconcileConfig.DeleteGuard"/>, never an operator
    /// override (there is no operator in this path to ask for one).
    /// </summary>
    public async Task<TriggerResult> EnqueueScheduledAsync(
        string replicationName, string mappingName, CancellationToken cancellationToken)
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

        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} source(s) and " +
                $"{mapping.Targets.Count} target(s); reconcile-deletes supports 1:1 mappings.");

        // Empty means Full — the same convention DefaultSegmenting already has for a standalone
        // reload's own configured segments.
        var requested = mapping.DefaultSegmenting.Count == 0
            ? [new FullSegment()]
            : mapping.DefaultSegmenting;

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, requested, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            return TriggerResult.Invalid(ex.Message);
        }

        var guard = PipelineResolution.Reconcile(task, mapping).DeleteGuard;
        var kinds = new WorkItemKinds(ReaderKind, CacheKind, WriterKind);
        var guardJson = DeleteGuardOption.Serialize(guard);

        var runIds = segments
            .Select(segment => workQueueStore.Enqueue(
                replicationName,
                RunKind.ReconcileDeletes,
                mappingName,
                segment.Describe(),
                SegmentSerializer.Serialize(segment),
                kinds,
                backfillBatchId: null,
                deleteGuardJson: guardJson))
            .ToList();

        var ensureResult = supervisor.EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>Mirrors <see cref="BackfillService"/>'s own segment expansion, but the reader is always
    /// <c>KeyReconcile</c> — there is no per-request reader Kind to resolve.</summary>
    private async Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, IReadOnlyList<BatchReloadSegment> requested,
        CancellationToken cancellationToken)
    {
        var needsAuto = requested.OfType<AutoSegment>().Any();
        var needsCustom = requested.OfType<CustomSegment>().Any();
        if (!needsAuto && !needsCustom)
            return requested;

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);

        var (connection, driver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        await using (connection)
        {
            IReadOnlyList<BatchReloadSegment> segments = requested;

            if (needsCustom)
            {
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

            var reader = driver.Readers.FirstOrDefault(r => r.Kind == ReaderKind)
                ?? throw new InvalidOperationException($"Source driver does not support reader kind '{ReaderKind}'.");

            if (reader is not ISegmentExpandingReader expanding)
                throw new InvalidOperationException(
                    $"Reader '{ReaderKind}' cannot divide a column into buckets, so an Auto segment can't be used " +
                    "with it. Specify explicit list/range segments instead.");

            return await expanding.ExpandAutoSegmentsAsync(connection, source, segments, cancellationToken);
        }
    }
}
