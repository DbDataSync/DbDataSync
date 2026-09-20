namespace DbDataSync.Api.Services;

/// <summary>
/// Whether the service is winding down for an update. While set, the scheduler enqueues nothing and the API
/// refuses to start anything new (<see cref="UpdateDrainMiddleware"/>), so the work already running can finish
/// and be the last of it.
/// <para>
/// A flag of its own rather than replication pause: pausing writes pause records and fires notifications, and
/// an update should do neither — it is not something an operator did to a replication, and it clears itself
/// by the process ending.
/// </para>
/// </summary>
public sealed class UpdateDrainState
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void Begin() => _draining = true;

    public void End() => _draining = false;
}
