namespace DataSync.Api.Services;

/// <summary>
/// Whether a replication is allowed to run at all, from the two independent gates that decide it.
/// <para>
/// One function rather than <c>enabled &amp;&amp; !paused</c> written at each call site. There are
/// already three places that ask — the scheduler's tick, the status the SPA renders, and the SPA
/// itself — and every one of them re-deriving it is three chances to add a third gate later and miss
/// one. The SPA in particular gets the answer rather than the inputs, so a rule change is a change
/// here and not in TypeScript as well.
/// </para>
/// </summary>
public static class TaskScheduling
{
    /// <param name="enabled">Config's durable intent (<c>ReplicationTaskConfig.Enabled</c>, git-tracked).</param>
    /// <param name="paused">State's temporary hold (<c>Tasks.Paused</c>, never committed).</param>
    public static bool ShouldRun(bool enabled, bool paused) => enabled && !paused;
}
