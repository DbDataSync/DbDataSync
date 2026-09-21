using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// When one run's stored watermarks were the source's own position — see phase 88.
/// </summary>
/// <param name="PreviousWatermarkTimeUtc">
/// The time behind <c>TaskRunRecord.PreviousWatermark</c>: where the mapping was before this pass.
/// </param>
/// <param name="NewWatermarkTimeUtc">The same for <c>NewWatermark</c>: where it got to.</param>
/// <remarks>
/// Either can be null on its own. A pass that advanced from a position the history has since purged
/// to one it still holds has a time for the second and not the first, and showing the one it has is
/// better than withholding both.
/// </remarks>
public sealed record RunWatermarkTimes(
    DateTimeOffset? PreviousWatermarkTimeUtc,
    DateTimeOffset? NewWatermarkTimeUtc);

/// <summary>
/// Turns the raw watermark strings on a page of run history into times, out of the polling history
/// the gate already keeps — see phase 88.
/// <para>
/// **The lookup is <c>ChangeCheckHistory</c>, not <c>ChangeWatermarks</c>, and that is the whole
/// design.** A mapping's <c>ChangeWatermarks</c> row is one row per mapping, overwritten — so it can
/// only ever match the run that most recently advanced it, and every older run's stored value would
/// have nothing to compare against. <c>ChangeCheckHistory</c> is the table that spans time: one row
/// per source group per gate tick, each carrying where the source had got to and when. The earliest
/// row whose value has reached a run's watermark is the earliest moment this system can say the
/// source was there, and that is the time this reports.
/// </para>
/// <para>
/// **Bounded by retention, and honest about it.** The history is age-purged (<c>RunPruningService</c>,
/// <c>DbDataSync:State:Retention:ChangeCheckDays</c>, seven days by default). A run older than that window has
/// no crossing row left to find, and gets no timestamp — the same "no data yet" discipline the rest
/// of this feature area uses, rather than an error or a value reconstructed from something else.
/// </para>
/// </summary>
public sealed class RunWatermarkTimeService(
    ChangeSourceResolver sources,
    ChangeCheckStore checks,
    ILogger<RunWatermarkTimeService> logger)
{
    /// <summary>
    /// Every run in <paramref name="runs"/> that has a time to show, keyed by run id.
    /// <para>
    /// A run is absent when neither of its watermarks resolved — which reads the same as present
    /// with two nulls, and keeps a page whose history has aged out from being mostly empty objects.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<Guid, RunWatermarkTimes> Describe(
        ReplicationTaskConfig task, IReadOnlyList<TaskRunRecord> runs)
    {
        // Every run of one mapping shares that mapping's source group, and every mapping reading the
        // same database by the same mechanism shares it too — so the resolution is cached per mapping
        // name and the lookups are then bucketed per group. A fifty-run page of one replication is
        // usually one group, and therefore one pass over the history.
        var resolved = new Dictionary<string, ChangeSource?>(StringComparer.Ordinal);
        var wanted = new Dictionary<ChangeSource, HashSet<string>>();
        var runGroups = new Dictionary<Guid, ChangeSource>();

        foreach (var run in runs)
        {
            if (Watermarks(run).Count == 0)
                continue;

            if (SourceOf(task, run.MappingName, resolved) is not { } source)
                continue;

            runGroups[run.RunId] = source;
            if (!wanted.TryGetValue(source, out var targets))
                wanted[source] = targets = new HashSet<string>(StringComparer.Ordinal);

            foreach (var watermark in Watermarks(run))
                targets.Add(watermark);
        }

        var crossings = new Dictionary<ChangeSource, IReadOnlyDictionary<string, ChangeCheck>>();
        foreach (var (source, targets) in wanted)
            crossings[source] = Crossings(source, targets);

        var times = new Dictionary<Guid, RunWatermarkTimes>();
        foreach (var run in runs)
        {
            if (!runGroups.TryGetValue(run.RunId, out var source))
                continue;

            var group = crossings[source];
            var previous = TimeOf(group, run.PreviousWatermark);
            var current = TimeOf(group, run.NewWatermark);
            if (previous is null && current is null)
                continue;

            times[run.RunId] = new RunWatermarkTimes(previous, current);
        }

        return times;
    }

    /// <summary>
    /// The crossing row for each target this group can still answer for.
    /// <para>
    /// **Targets beyond the newest recorded value are dropped before the scan, not after it.** They
    /// are the ones that cannot resolve — nothing in the history has reached them — and they are
    /// also the common case, because the newest run's <c>NewWatermark</c> is routinely past the last
    /// position the gate wrote down. Left in, each one would hold the batched lookup open to the end
    /// of the table on every request; dropped, the scan stops at the largest target that is actually
    /// reachable.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, ChangeCheck> Crossings(
        ChangeSource source, IReadOnlyCollection<string> targets)
    {
        var newest = checks.GetLatestCheck(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind)?.Value;

        var reachable = newest is null
            ? []
            : targets.Where(target => Reaches(source.ReaderKind, newest, target)).ToList();

        return checks.FindEarliestChecks(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind, reachable,
            (value, target) => Reaches(source.ReaderKind, value, target));
    }

    /// <summary>
    /// The engine's own time for the crossing row, or the moment we polled it.
    /// <para>
    /// <c>SourceTimeUtc</c> is when the source says that position committed and <c>CheckedAtUtc</c>
    /// is when this system happened to ask — the first is the better answer wherever the mechanism
    /// has a time mapping and the engine still holds it, and the second is a real answer rather than
    /// a missing one where it does not.
    /// </para>
    /// </summary>
    private static DateTimeOffset? TimeOf(
        IReadOnlyDictionary<string, ChangeCheck> crossings, string? watermark) =>
        string.IsNullOrEmpty(watermark) || !crossings.TryGetValue(watermark, out var check)
            ? null
            : check.SourceTimeUtc ?? check.CheckedAtUtc;

    /// <summary>
    /// Whether a recorded value has got to or past a target, in the mechanism's own ordering.
    /// <para>
    /// Guarded, because both operands here are strings out of two different tables and a value that
    /// will not parse as this mechanism's counter is a row somebody stored wrongly rather than a
    /// reason a run list should fail to render. Unparseable means "cannot say this was reached",
    /// which is the same answer as no crossing row at all.
    /// </para>
    /// </summary>
    private bool Reaches(string readerKind, string value, string target)
    {
        try
        {
            return ChangeCounters.Compare(readerKind, value, target) >= 0;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            logger.LogDebug(
                ex, "Could not compare '{Value}' with '{Target}' as a {Kind} position.",
                value, target, readerKind);
            return false;
        }
    }

    private ChangeSource? SourceOf(
        ReplicationTaskConfig task, string mappingName, Dictionary<string, ChangeSource?> cache)
    {
        if (cache.TryGetValue(mappingName, out var cached))
            return cached;

        try
        {
            // The same resolution the polling gate and the lag service use, so a run's watermark is
            // looked up in exactly the group the gate wrote its history into.
            cache[mappingName] = sources.Describe(task, mappingName).Source;
        }
        catch (Exception ex)
        {
            // A mapping whose config will not load — deleted since the run, most likely — has no
            // timestamps to show and is not a server error, the same judgement ReaderLagService
            // makes about the same failure.
            logger.LogWarning(
                ex, "Could not resolve the source of '{Task}'/'{Mapping}' to date its watermarks.",
                task.Name, mappingName);
            cache[mappingName] = null;
        }

        return cache[mappingName];
    }

    /// <summary>
    /// The watermarks a run actually stored. Both are null for a run that made no new position
    /// durable — a bulk load, a verification, or any failed pass — which is most of what a filtered
    /// history shows and none of what this has to look up.
    /// </summary>
    private static List<string> Watermarks(TaskRunRecord run)
    {
        var watermarks = new List<string>(2);
        if (!string.IsNullOrEmpty(run.PreviousWatermark)) watermarks.Add(run.PreviousWatermark);
        if (!string.IsNullOrEmpty(run.NewWatermark)) watermarks.Add(run.NewWatermark);
        return watermarks;
    }
}
