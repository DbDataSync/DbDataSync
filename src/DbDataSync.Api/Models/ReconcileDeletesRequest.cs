using DbDataSync.Core.Config;

namespace DbDataSync.Api.Models;

/// <summary>
/// A request to sweep one table mapping's target for rows deleted at the source — phase 124.
/// Engine-neutral, the same shape <see cref="BackfillRequest"/> already establishes for segments; no
/// reader/cache/writer Kind fields, because a reconcile sweep always runs
/// <c>KeyReconcile</c>/<c>StagingTable</c>/<c>KeyReconcileDelete</c> — there is nothing else to pick.
/// </summary>
public sealed class ReconcileDeletesRequest
{
    /// <summary>Same contract as <see cref="BackfillRequest.Segments"/>: at least one, an <c>Auto</c>
    /// entry expanded server-side against the source's real value range before anything is enqueued.</summary>
    public List<BatchReloadSegment> Segments { get; set; } = [];

    /// <summary>Replaces the writer's configured/default <see cref="DeleteGuard"/> with
    /// <see cref="NoneDeleteGuard"/> for every segment this request enqueues — an operator confirming
    /// "yes, delete this many rows" after a guarded attempt refused.</summary>
    public bool OverrideGuard { get; set; }
}
