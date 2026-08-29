using DataSync.Api.Models;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// The recovery for a run that failed because the source discarded the history it needed — see
/// <c>PositionExpiredException</c>.
/// <para>
/// It is **offered, not performed**. A full reload of a table that fell behind can be hours of work,
/// and starting it automatically because a pass failed would be a large operation nobody asked for.
/// So the run fails with a distinct outcome, the Runs tab shows a Resync action, and this is what that
/// action does.
/// </para>
/// <para>
/// Two things, because doing one without the other leaves the replication broken in a different way:
/// a full reload brings the target's contents current, and clearing the stored watermark is what lets
/// the incremental pass start again — an operator constructing this by hand would do the first and
/// forget the second, and the next pass would fail exactly as before.
/// </para>
/// </summary>
public sealed class ResyncService(
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    ChangeWatermarkStore watermarks,
    BackfillService backfill)
{
    public async Task<TriggerResult> ResyncAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = taskRunStore.GetRun(runId);
        if (run is null)
            return TriggerResult.NotFound();

        // Only for the failure this exists to answer. Offering it on an arbitrary failed run would be
        // offering a full reload as a general-purpose retry button.
        if (run.FailureKind != RunFailureKinds.PositionExpired)
        {
            return TriggerResult.Invalid(
                "This run did not fail because its source position expired, so a resync is not the " +
                "fix for it. Read the run's error and log first.");
        }

        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(run.TaskName);
            mapping = configRepository.LoadTableMapping(run.TaskName, run.MappingName);
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        if (mapping.Sources.Count != 1)
            return TriggerResult.Invalid(
                $"Table mapping '{run.MappingName}' has {mapping.Sources.Count} sources; resync supports 1:1 mappings.");

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);

        // Cleared before the reload is enqueued, not after. If this process dies in between, a
        // replication whose watermark is gone reads the table from the beginning on its next pass —
        // which is slow and correct. The other order leaves a reload running against a watermark that
        // is still expired, so the next incremental pass fails again and the operator is told to do
        // the thing they just did.
        watermarks.ClearWatermark(run.TaskName, WatermarkKey.Build(source));

        // The reload reader, because the configured one reports changes since a watermark and there
        // is no usable watermark — that is the whole problem. The engine-neutral one: a resync is not
        // a place to care which engine this is.
        var request = new BackfillRequest
        {
            ReaderKind = GenericDriverKinds.BatchReload,
            Segments = [new FullSegment()],
        };

        return await backfill.EnqueueAsync(run.TaskName, run.MappingName, request, cancellationToken);
    }
}
