using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Decides which of a tick's due CDC and Change-Tracking mappings actually have work waiting, so a
/// quiet source database costs one round-trip per tick instead of one per mapping — see phase 75.
/// <para>
/// **A collaborator rather than more of <see cref="SchedulerService"/>.** The scheduler is a loop over
/// config and local state whose entire body is due-ness arithmetic; this is source I/O, grouping,
/// per-mechanism comparison and a fail-open policy, none of which it previously had any of. Kept
/// apart, the gate is testable without an API host and the scheduler still reads as the schedule it
/// is.
/// </para>
/// <para>
/// **The comparison is against each mapping's own watermark, never against a remembered value.** The
/// obvious design — cache what the counter read last tick, skip everything when it has not moved —
/// ships a real bug. <c>MsSqlChangeTrackingReader</c> supports bounded reads: a mapping draining a
/// backlog under a row cap advances its own watermark only as far as the rows it processed, so it can
/// need many passes to catch up while no new writes happen at the source at all. The database-wide
/// counter does not move across those passes, and a gate comparing against its own last reading would
/// read that as "nothing to do" and stall the mapping mid-drain, for as long as its schedule kept
/// coming round. Asking instead whether <em>this mapping</em> is behind <em>this counter</em> is
/// correct for bounded and unbounded readers alike, and needs no stored state of its own: the
/// watermark is already local (phase 74), so the per-mapping half of the decision costs no
/// round-trip.
/// </para>
/// </summary>
public sealed class ChangePollingGate(
    ChangeSourceResolver sources,
    IChangeCounterSource counters,
    ChangeWatermarkStore watermarks,
    ChangeCheckStore checks,
    ILogger<ChangePollingGate> logger)
{
    /// <summary>A due mapping and the group its source round-trip belongs to.</summary>
    private sealed record Candidate(string MappingName, string WatermarkKey, string ReaderKind);

    /// <summary>
    /// Narrows this tick's due mappings to those with work waiting. Anything the gate cannot answer
    /// for — a reader kind with no database-wide counter, a mapping whose config will not resolve, a
    /// source that will not answer — comes back in the list, because the behaviour this phase must
    /// never regress is the one it started from: dispatch.
    /// </summary>
    public async Task<IReadOnlyList<string>> AdmitAsync(
        ReplicationTaskConfig task,
        IReadOnlyList<string> dueMappings,
        CancellationToken cancellationToken)
    {
        // Grouped by (ConnectionName, Database, SourceKind). SourceKind is in the key and not merely
        // alongside it: one database can have CDC on one table and Change Tracking on another, and
        // their counters are a byte[] log position and a bigint version. Sharing a group would mean
        // one fetch answering for a mechanism it was not taken from, and one audit row holding a
        // value in a format its neighbour cannot parse — the collision phase 74 removed from
        // ChangeWatermarks, reintroduced a layer up.
        var groups = new Dictionary<(string Connection, string Database, string Kind), List<Candidate>>();

        // A set of names, then one filter over the original list at the end, so what comes back is in
        // the order the caller asked in. Grouping reorders by construction, and the scheduler's
        // enqueue order is the order mappings are declared in — worth not scrambling for a reason
        // that has nothing to do with the schedule.
        var admitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mappingName in dueMappings)
        {
            if (!TryDescribe(task, mappingName, out var group, out var candidate))
            {
                admitted.Add(mappingName);
                continue;
            }

            if (!groups.TryGetValue(group, out var members))
                groups[group] = members = [];
            members.Add(candidate!);
        }

        foreach (var (group, members) in groups)
        {
            ChangeCounterReading reading;
            try
            {
                reading = await counters.FetchAsync(
                    group.Connection, group.Database, group.Kind, cancellationToken);
            }
            catch (Exception ex)
            {
                // Fail open, and only for this group. The scheduler had no source dependency before
                // this phase, and an unreachable database must not become a way to stop replication
                // being scheduled — including the replication of the databases that are still up.
                logger.LogWarning(
                    ex,
                    "Change poll for '{Connection}'/'{Database}' ({Kind}) failed; dispatching its " +
                    "{Count} due mapping(s) unfiltered.",
                    group.Connection, group.Database, group.Kind, members.Count);
                foreach (var member in members)
                    admitted.Add(member.MappingName);
                continue;
            }

            RecordCheck(group, reading);

            foreach (var member in members)
            {
                if (ShouldDispatch(task.Name, member, reading.Value))
                    admitted.Add(member.MappingName);
            }
        }

        return [.. dueMappings.Where(admitted.Contains)];
    }

    /// <summary>
    /// Whether one mapping is behind the counter its group just fetched.
    /// <para>
    /// Three ways to answer yes, and only one to answer no. A null counter is the source declining to
    /// state a position at all (CDC before its capture job has run), which is not evidence of
    /// quiet. A mapping with no stored watermark has never read, and a first pass is not this
    /// mechanism's to gate — there is nothing to be caught up with. A value that will not parse is a
    /// disagreement between what is stored and what the mechanism expects, which a reader should be
    /// allowed to run into and report rather than have the scheduler silently sit on.
    /// </para>
    /// </summary>
    private bool ShouldDispatch(string taskName, Candidate member, string? counter)
    {
        if (counter is null)
            return true;

        var stored = watermarks.GetWatermark(taskName, member.MappingName, member.WatermarkKey);
        if (string.IsNullOrEmpty(stored))
            return true;

        try
        {
            return ChangeCounters.Compare(member.ReaderKind, stored, counter) < 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not compare '{Mapping}''s stored watermark against the {Kind} counter; " +
                "dispatching it.",
                member.MappingName, member.ReaderKind);
            return true;
        }
    }

    private void RecordCheck(
        (string Connection, string Database, string Kind) group, ChangeCounterReading reading)
    {
        try
        {
            checks.Record(
                group.Connection, group.Database, group.Kind, reading.Value, reading.SourceTimeUtc);
        }
        catch (Exception ex)
        {
            // History, not mechanism: the dispatch decision this tick has already been made from the
            // fetched value itself, so a state database that is briefly locked costs an audit line
            // and a later lag figure's precision, never a mapping's pass.
            logger.LogWarning(
                ex, "Could not record the change check for '{Connection}'/'{Database}' ({Kind}).",
                group.Connection, group.Database, group.Kind);
        }
    }

    /// <summary>
    /// Resolves what the gate needs about one mapping, or says it cannot. False means "not this
    /// gate's business or not answerable" — an ungated reader kind, a mapping with more than one
    /// source, config that will not load — and every one of those is dispatched unchanged.
    /// </summary>
    private bool TryDescribe(
        ReplicationTaskConfig task,
        string mappingName,
        out (string Connection, string Database, string Kind) group,
        out Candidate? candidate)
    {
        group = default;
        candidate = null;

        try
        {
            if (sources.Describe(task, mappingName).Source is not { } source)
                return false;

            group = (source.ConnectionName, source.SourceDatabase, source.ReaderKind);
            candidate = new Candidate(mappingName, source.WatermarkKey, source.ReaderKind);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not evaluate the change poll for '{Task}'/'{Mapping}'; dispatching it.",
                task.Name, mappingName);
            return false;
        }
    }
}
