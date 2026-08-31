using DataSync.Api.Models;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// Turns a backfill request into queued work: expand any <see cref="AutoSegment"/> against the real
/// source table, then enqueue one independently-scheduled unit of work per resulting segment.
/// <para>
/// Nothing spawns a process on this request's critical path. Enqueueing is a handful of SQLite writes,
/// which is what makes "queue hundreds of segments at once" a reasonable thing to do — the worker is
/// merely nudged into existence afterwards, and that's a no-op if one is already draining.
/// </para>
/// </summary>
public sealed class BackfillService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    WorkQueueStore workQueueStore,
    ProcessSupervisor supervisor,
    CustomSegmentExpansion customSegments)
{
    public async Task<TriggerResult> EnqueueAsync(
        string replicationName, string mappingName, BackfillRequest request, CancellationToken cancellationToken)
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
                $"Table mapping '{mappingName}' has {mapping.Sources.Count} sources; backfill supports 1:1 mappings in v1.");

        IReadOnlyList<BatchReloadSegment> segments;
        try
        {
            segments = await ExpandAsync(task, mapping, request, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            // A bad segment column, an undividable column type, or an unreachable source are all the
            // request being wrong or unrunnable — not a server fault, and worth saying plainly rather
            // than enqueueing work that is guaranteed to fail once a worker claims it.
            return TriggerResult.Invalid(ex.Message);
        }

        var kinds = new WorkItemKinds(request.ReaderKind, request.CacheKind, request.WriterKind);
        var runIds = segments
            .Select(segment => workQueueStore.Enqueue(
                replicationName,
                RunKind.Backfill,
                mappingName,
                segment.Describe(),
                SegmentSerializer.Serialize(segment),
                kinds))
            .ToList();

        var ensureResult = supervisor.EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>
    /// Resolves <see cref="AutoSegment"/>s into concrete ranges. Done here, once, rather than in the
    /// worker: the bounds come from the source's real MIN/MAX, and each resulting range has to become
    /// its own queue row so segments can be scheduled, retried and observed independently.
    /// </summary>
    private async Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, BackfillRequest request, CancellationToken cancellationToken)
    {
        var needsAuto = request.Segments.OfType<AutoSegment>().Any();
        var needsCustom = request.Segments.OfType<CustomSegment>().Any();
        if (!needsAuto && !needsCustom)
            return request.Segments;

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var readerKind = PipelineResolution.ReaderKind(request.ReaderKind, task, mapping);

        var (connection, driver) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        await using (connection)
        {
            IReadOnlyList<BatchReloadSegment> segments = request.Segments;

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

            return await expanding.ExpandAutoSegmentsAsync(connection, source, segments, cancellationToken);
        }
    }
}
