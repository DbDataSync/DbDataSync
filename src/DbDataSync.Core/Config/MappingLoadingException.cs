namespace DbDataSync.Core.Config;

/// <summary>
/// A <c>Primary</c> pass resolved against a mapping whose <see cref="ReadHold"/> is
/// <see cref="ReadHold.Loading"/> — an initial load is in flight and nothing durable backs an
/// incremental read yet (only <c>ChangeWatermarks.PendingWatermark</c> is set; the live
/// <c>Watermark</c> stays null until the load completes).
/// <para>
/// <c>SchedulerService.FilterHeld</c> is the enforcement point for this hold on the *scheduled* path,
/// deliberately not the manual one — a manual "Run Now" bypassing it is by design, so an operator
/// re-triggering a held mapping notices something is happening. What was missing was what "letting it
/// through" actually produced: without this check, a Primary pass against a mapping in this exact state
/// fell through to whichever reader's <c>Changes</c> branch happened to be configured, most of which
/// assume a non-null stored watermark and throw something unrelated (an <c>ArgumentNullException</c>
/// from <c>long.Parse</c>, for <c>MsSqlChangeTrackingReader</c>) — a mystery failure for a state that was
/// already known and named. See the follow-up doc this replaces: a manual "Run Now" against a still-
/// Loading mapping crashes with an unhelpful exception.
/// </para>
/// <para>
/// Thrown once, before any reader is ever dispatched to (Primary passes only — this must never affect
/// the BulkLoad this hold is itself protecting), so the message is the same regardless of which reader
/// is configured, rather than depending on every reader's own <c>Changes</c> branch to notice a null
/// watermark gracefully.
/// </para>
/// </summary>
/// <param name="taskName">The replication this mapping belongs to.</param>
/// <param name="mappingName">The mapping still loading.</param>
public sealed class MappingLoadingException(string taskName, string mappingName)
    : Exception(
        $"Mapping '{mappingName}' on '{taskName}' is still loading — an initial load is in flight and " +
        "its watermark is not durable yet. This pass will retry automatically once the load completes.")
{
    public string TaskName { get; } = taskName;
    public string MappingName { get; } = mappingName;
}
