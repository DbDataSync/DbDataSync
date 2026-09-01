using DataSync.Core.Config;
using DataSync.Drivers.MsSql;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// How far behind its source one table mapping is, in whatever units its mechanism can honestly
/// answer in — see phase 85.
/// </summary>
/// <param name="ReaderKind">The mechanism the figures below come from, named even when there are
/// none, so "this reader cannot report lag" is a statement rather than an empty response.</param>
/// <param name="Supported">
/// False for every reader with no database-wide position to compare against — the generic watermark
/// reader, batch reload, trigger audit. **Not the same as a supported mechanism with nothing to
/// report yet**, which is <c>Supported</c> true and null figures: the first is "never ask", the
/// second is "ask again later".
/// </param>
/// <param name="ExactLag">
/// A real duration, from the engine's own position-to-time mapping at **both** ends of the
/// subtraction — <c>fn_cdc_map_lsn_to_time</c> for CDC, <c>dm_tran_commit_table</c> for Change
/// Tracking. Null when the mapping has never read, or when its position has aged out of the window
/// the source still maps; for Change Tracking that null is what makes
/// <paramref name="EstimatedLag"/> get computed at all.
/// </param>
/// <param name="ExactVersionsBehind">
/// Change Tracking only. A count of versions, exact by construction — both numbers are already
/// stored, and neither is estimated. Not a time, and not convertible into one: what a version is
/// worth depends entirely on how often the source's tables are written to.
/// </param>
/// <param name="EstimatedLag">
/// Change Tracking's fallback, and **never comparable with <paramref name="ExactLag"/>** — the two
/// are separate properties precisely so that nothing downstream can average, chart or threshold them
/// together. Populated only when <c>dm_tran_commit_table</c> could not place the mapping's version,
/// which means the version has aged out of that DMV's rolling window; the figure is then
/// reconstructed from when this system's own polling first observed a version at or above the
/// mapping's, so its precision is bounded by the polling interval rather than by anything about the
/// data. Null — never a synthesised zero — when the group has not accumulated enough history to
/// place the mapping's version at all.
/// </param>
public sealed record ReaderLag(
    string ReaderKind,
    bool Supported,
    TimeSpan? ExactLag = null,
    long? ExactVersionsBehind = null,
    TimeSpan? EstimatedLag = null);

/// <summary>
/// Computes a mapping's staleness against its source's own last-known position.
/// <para>
/// **Never against wall-clock now, in any of the three figures.** "Now minus the time of the last
/// change we applied" is the obvious definition and it is wrong for a reason that only shows up in
/// the good case: on a source nobody is writing to, a fully caught-up replication's lag under that
/// definition grows by a second every second, for ever, and an alert built on it fires precisely when
/// there is nothing to do. Every comparison here has the source's own current position on the other
/// side of it, so a caught-up mapping reads zero and stays there.
/// </para>
/// <para>
/// **What "the source's current position" means is a history row, not a fresh fetch.** The polling
/// gate (phase 75) already asks each source group once a tick and writes down what it saw, so a lag
/// call takes the far end of every subtraction from that rather than polling the source a second
/// time. CDC falls back to a live fetch when no usable row exists at all, which is a first-install
/// condition rather than a steady state.
/// </para>
/// <para>
/// **Turning a position into a time is the part that still asks the engine**, in both mechanisms,
/// because the position being asked about is one mapping's and the history row is a whole group's —
/// there is nowhere shared to cache it. CDC maps its own watermark through
/// <c>fn_cdc_map_lsn_to_time</c>; Change Tracking maps both ends through
/// <c>sys.dm_tran_commit_table</c> and, only when that DMV has aged the version out, reconstructs an
/// estimate from polling history and reports it under a different name.
/// </para>
/// </summary>
public sealed class ReaderLagService(
    ChangeSourceResolver sources,
    IChangeCounterSource counters,
    ChangeWatermarkStore watermarks,
    ChangeCheckStore checks,
    ILogger<ReaderLagService> logger)
{
    public async Task<ReaderLag> DescribeAsync(
        ReplicationTaskConfig task, string mappingName, CancellationToken cancellationToken)
    {
        string readerKind;
        ChangeSource? source;
        try
        {
            (readerKind, source) = sources.Describe(task, mappingName);
        }
        catch (Exception ex)
        {
            // A mapping whose config will not resolve has no lag to report and is not a server error
            // — the same judgement the gate makes about the same failure, which there means "dispatch
            // anyway" and here means "no figures".
            logger.LogWarning(
                ex, "Could not resolve the source of '{Task}'/'{Mapping}' to report its lag.",
                task.Name, mappingName);
            return new ReaderLag("Unknown", Supported: false);
        }

        if (source is null)
            return new ReaderLag(readerKind, Supported: false);

        var applied = watermarks.GetWatermark(task.Name, mappingName, source.WatermarkKey);

        // No stored position at all: the mapping has never completed a pass, so there is nothing to
        // measure from. Null rather than a large number — "unknown" and "far behind" are different
        // states and only one of them is worth waking somebody for.
        if (string.IsNullOrEmpty(applied))
            return new ReaderLag(readerKind, Supported: true);

        return readerKind switch
        {
            MsSqlDriverKinds.Cdc => new ReaderLag(
                readerKind,
                Supported: true,
                ExactLag: await CdcLagAsync(source, applied, cancellationToken)),

            MsSqlDriverKinds.ChangeTracking =>
                await ChangeTrackingLagAsync(readerKind, source, applied, cancellationToken),

            _ => new ReaderLag(readerKind, Supported: false),
        };
    }

    /// <summary>
    /// CDC's real duration: the time the source puts on its own current position, minus the time it
    /// puts on this mapping's position. Both ends come from <c>fn_cdc_map_lsn_to_time</c> on the same
    /// server, so whatever clock that server keeps cancels out of the difference.
    /// </summary>
    private async Task<TimeSpan?> CdcLagAsync(
        ChangeSource source, string applied, CancellationToken cancellationToken)
    {
        try
        {
            // The far end first, because it is the one that can be answered without a round-trip.
            // Deliberately the latest row that *has* a time rather than the latest row: a group whose
            // most recent poll found no position at all (the capture job stopped) still has an
            // earlier reading that a lag can be measured against, and reporting nothing there would
            // hide a lag that is growing for exactly that reason.
            var current =
                checks.GetLatestCheck(
                    source.ConnectionName, source.SourceDatabase, source.ReaderKind,
                    requireSourceTime: true)?.SourceTimeUtc
                ?? (await counters.FetchAsync(
                    source.ConnectionName, source.SourceDatabase, source.ReaderKind, cancellationToken))
                    .SourceTimeUtc;

            if (current is null)
                return null;

            var appliedTime = await counters.MapSourceTimeAsync(
                source.ConnectionName, source.SourceDatabase, source.ReaderKind, applied, cancellationToken);

            return appliedTime is null ? null : Clamp(current.Value - appliedTime.Value);
        }
        catch (Exception ex)
        {
            // Reading lag is a question, not a pass. An unreachable source means the answer is not
            // available right now, which is what null says; nothing about the replication changes.
            logger.LogWarning(
                ex, "Could not compute CDC lag for '{Connection}'/'{Database}'.",
                source.ConnectionName, source.SourceDatabase);
            return null;
        }
    }

    /// <summary>
    /// Change Tracking's figures: an exact version count, and a time that is exact when the engine
    /// will still state it and an estimate when it will not.
    /// <para>
    /// The version count is arithmetic on two numbers that are already stored — no round-trip, no
    /// estimation.
    /// </para>
    /// <para>
    /// **The time tries the engine first.** <c>sys.dm_tran_commit_table</c> maps a commit sequence
    /// number to its commit time, and a <c>SYS_CHANGE_VERSION</c> is a commit sequence number, so
    /// Change Tracking does have the equivalent of <c>fn_cdc_map_lsn_to_time</c> after all. When it
    /// answers for both ends the figure is exact in the same sense CDC's is, and is reported in the
    /// same field.
    /// </para>
    /// <para>
    /// **The estimate is the fallback, not the mechanism.** The DMV holds a rolling window, so a
    /// mapping far enough behind — precisely the one worth reporting on — can have a version that has
    /// aged out of it. Only then does this reconstruct a figure from this system's own polling: the
    /// first check that observed a version at or above the mapping's is the earliest moment we can
    /// say the source had reached where the mapping now is, and the distance from there to the latest
    /// check is how long it has been behind. Bounded above by the poll interval, reported under a
    /// name that says so.
    /// </para>
    /// </summary>
    private async Task<ReaderLag> ChangeTrackingLagAsync(
        string readerKind, ChangeSource source, string applied, CancellationToken cancellationToken)
    {
        if (!long.TryParse(applied, out var appliedVersion))
            return new ReaderLag(readerKind, Supported: true);

        var latest = checks.GetLatestCheck(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind);

        // Never polled this group: both figures are measured against its latest check, and there
        // isn't one. The exact figure could be recovered with a live fetch, but a lag call is a
        // read of a status screen and the gate will have written a row within a tick.
        if (latest is null)
            return new ReaderLag(readerKind, Supported: true);

        long? behind = latest.Value is not null && long.TryParse(latest.Value, out var current)
            ? Clamp(current - appliedVersion)
            : null;

        if (await ExactChangeTrackingLagAsync(source, appliedVersion, latest, cancellationToken) is { } exact)
            return new ReaderLag(
                readerKind, Supported: true, ExactLag: exact, ExactVersionsBehind: behind);

        // Parsed as numbers, in this language, one row at a time — not a >= in the SQL. Value is
        // text in a store that runs on three engines, and every one of them would order "9" above
        // "10" and anchor the estimate on a row from before the version it is looking for.
        var crossing = checks.FindEarliestCheck(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind,
            value => long.TryParse(value, out var seen) && seen >= appliedVersion);

        return new ReaderLag(
            readerKind,
            Supported: true,
            ExactVersionsBehind: behind,
            EstimatedLag: crossing is null ? null : Clamp(latest.CheckedAtUtc - crossing.CheckedAtUtc));
    }

    /// <summary>
    /// The exact Change Tracking duration, or null to say the engine could not give one.
    /// <para>
    /// **Both ends come from the DMV, or neither is used.** Subtracting an exact commit time from the
    /// wall-clock instant a poll happened to run would be a hybrid whose error is the polling
    /// interval — exactly the error the estimate already owns and names — while landing in the field
    /// that promises there is no such error. The mapping's own version is looked up first because it
    /// is the older of the two and therefore the one that ages out; when it has, the second query is
    /// not worth making.
    /// </para>
    /// </summary>
    private async Task<TimeSpan?> ExactChangeTrackingLagAsync(
        ChangeSource source, long appliedVersion, ChangeCheck latest, CancellationToken cancellationToken)
    {
        if (latest.Value is null)
            return null;

        try
        {
            var appliedTime = await counters.MapSourceTimeAsync(
                source.ConnectionName, source.SourceDatabase, source.ReaderKind,
                appliedVersion.ToString(), cancellationToken);

            if (appliedTime is null)
                return null;

            var currentTime = await counters.MapSourceTimeAsync(
                source.ConnectionName, source.SourceDatabase, source.ReaderKind,
                latest.Value, cancellationToken);

            return currentTime is null ? null : Clamp(currentTime.Value - appliedTime.Value);
        }
        catch (Exception ex)
        {
            // Unlike CDC, there is somewhere to go from here: the estimate needs no source at all.
            // An unreachable source costs precision, not the figure.
            logger.LogWarning(
                ex, "Could not map Change Tracking versions to commit times for '{Connection}'/'{Database}'; "
                    + "falling back to the polling-history estimate.",
                source.ConnectionName, source.SourceDatabase);
            return null;
        }
    }

    /// <summary>
    /// Negative is not a lag, it is a mapping that is ahead of the last thing we wrote down about its
    /// source — which happens legitimately, when a pass runs between two polls and reads past the
    /// position the earlier one recorded. Zero, because that mapping is not behind.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan lag) => lag < TimeSpan.Zero ? TimeSpan.Zero : lag;

    private static long Clamp(long behind) => behind < 0 ? 0 : behind;
}
