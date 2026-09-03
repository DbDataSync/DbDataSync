using DbDataSync.Core.Config;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

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
/// <param name="AsOfUtc">
/// The far end of the subtraction: where the source had got to, at the moment the figures above are
/// measured against. Phase 88 surfaces what phase 85 already computed and discarded.
/// <para>
/// **The exact <c>ChangeCheckHistory</c> row this mapping's own comparison used**, not the group's
/// newest row and not the clock. CDC and Change Tracking pick that row differently — CDC takes the
/// latest one that carries a source time, Change Tracking the latest one outright — so a figure and
/// the "as of" beside it always come from the same reading.
/// </para>
/// <para>
/// <c>SourceTimeUtc</c> where the row has one and <c>CheckedAtUtc</c> where it does not, matching
/// whichever the figure itself used. The two are different claims — when the engine says that
/// position committed, versus when this system happened to ask — and the first is the better answer
/// exactly where it exists.
/// </para>
/// <para>
/// Null when no history row was consulted at all: an unsupported reader, or a mapping that has never
/// stored a position, both of which return before any check is read.
/// </para>
/// </param>
public sealed record ReaderLag(
    string ReaderKind,
    bool Supported,
    TimeSpan? ExactLag = null,
    long? ExactVersionsBehind = null,
    TimeSpan? EstimatedLag = null,
    DateTimeOffset? AsOfUtc = null);

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
/// **This service does no I/O against any source, in any path, ever — see phase 87.** It has no
/// <c>IChangeCounterSource</c> to call, which is the strongest available statement of that: the
/// property is not a rule anyone has to keep, it is a dependency that is not there. Both ends of
/// every subtraction were cached at a moment when they were free, and this reads them back:
/// </para>
/// <para>
/// **The source's current position and its time** come from a <c>ChangeCheckHistory</c> row. The
/// polling gate (phase 75) asks each source group once a tick and writes down both, on the connection
/// it opened for its own reason.
/// </para>
/// <para>
/// **The mapping's own applied position and its time** come from its <c>ChangeWatermarks</c> row.
/// The reader that produced the position mapped it to a time on the connection the pass already had
/// open, and both were written in the same statement — a position and a time that could disagree
/// about which pass they came from would be a wrong lag rather than a missing one.
/// </para>
/// <para>
/// Phase 85 asked the engine for the applied-position time on every call, on the reasoning that a
/// mapping's position has nowhere shared to be cached. It has somewhere of its own, which is better:
/// "when did this position commit" is a fixed historical fact from the moment it is known, so
/// recording it right after the pass that produced it is *more* reliable than re-asking later — for
/// Change Tracking decisively so, since a promptly captured version is captured before
/// <c>dm_tran_commit_table</c>'s rolling window could ever have aged it out.
/// </para>
/// <para>
/// **A missing cached value is "no data yet", never a live escape hatch.** A mapping that has not run
/// since phase 87 shipped, or whose reader could not place its position, reports <c>Supported</c>
/// true with null figures — the state this service already used for a mapping that has never read. A
/// cold start resolves itself within one scheduling interval; a live fallback would be a permanent
/// licence for a status screen to poll a production source.
/// </para>
/// </summary>
public sealed class ReaderLagService(
    ChangeSourceResolver sources,
    ChangeWatermarkStore watermarks,
    ChangeCheckStore checks,
    ILogger<ReaderLagService> logger)
{
    /// <summary>
    /// **Synchronous, and that is the phase 87 property stated in the signature.** Every value this
    /// reads is a row in the state database, which every store here reads synchronously; there is no
    /// round-trip left to await. A caller cannot accidentally reintroduce one without changing this
    /// shape.
    /// </summary>
    public ReaderLag Describe(ReplicationTaskConfig task, string mappingName)
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

        var applied = watermarks.GetAppliedPosition(task.Name, mappingName, source.WatermarkKey);

        // No stored position at all: the mapping has never completed a pass, so there is nothing to
        // measure from. Null rather than a large number — "unknown" and "far behind" are different
        // states and only one of them is worth waking somebody for.
        if (applied is null || string.IsNullOrEmpty(applied.Watermark))
            return new ReaderLag(readerKind, Supported: true);

        return readerKind switch
        {
            MsSqlDriverKinds.Cdc => CdcLag(readerKind, source, applied),

            MsSqlDriverKinds.ChangeTracking => ChangeTrackingLag(readerKind, source, applied),

            _ => new ReaderLag(readerKind, Supported: false),
        };
    }

    /// <summary>
    /// CDC's real duration: the time the source put on its own current position, minus the time it
    /// put on this mapping's position. Both come from <c>fn_cdc_map_lsn_to_time</c> on the same
    /// server, so whatever clock that server keeps cancels out of the difference — and both were
    /// asked for at a moment that cost nothing, which is why neither is asked for here.
    /// <para>
    /// **No live fallback, in either direction.** Phase 85 fetched the group's position live when no
    /// history row carried a time, calling it a first-install condition; it is, and a first install
    /// resolves itself on the next gate tick. Keeping the branch meant every status screen retained a
    /// path to the source, and the caller most likely to take it — a mapping on a source that has
    /// just come back after an outage — is exactly the one that should not be adding load.
    /// </para>
    /// </summary>
    private ReaderLag CdcLag(string readerKind, ChangeSource source, AppliedPosition applied)
    {
        // Deliberately the latest row that *has* a time rather than the latest row: a group whose
        // most recent poll found no position at all (the capture job stopped) still has an earlier
        // reading that a lag can be measured against, and reporting nothing there would hide a lag
        // that is growing for exactly that reason.
        var current = checks.GetLatestCheck(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind,
            requireSourceTime: true)?.SourceTimeUtc;

        // Reported even where the figure is not. "We last saw the source here, and cannot yet say
        // how far behind you are" is a more useful screen than a blank one, and it is the difference
        // between a stalled poller and a mapping that has simply not run.
        var lag = new ReaderLag(readerKind, Supported: true, AsOfUtc: current);

        if (current is null || applied.WatermarkTimeUtc is null)
        {
            // Two different absences, one answer. The group has never been polled with a placeable
            // position, or the mapping has not run since its cached time became a thing to record —
            // and in both cases the honest report is that there is no figure yet, not a number
            // reconstructed from whatever else is lying around.
            logger.LogDebug(
                "No cached lag inputs yet for '{Connection}'/'{Database}': current={Current}, applied={Applied}.",
                source.ConnectionName, source.SourceDatabase, current, applied.WatermarkTimeUtc);
            return lag;
        }

        return lag with { ExactLag = Clamp(current.Value - applied.WatermarkTimeUtc.Value) };
    }

    /// <summary>
    /// Change Tracking's figures: an exact version count, and a time that is exact when the engine
    /// will still state it and an estimate when it will not.
    /// <para>
    /// The version count is arithmetic on two numbers that are already stored — no round-trip, no
    /// estimation.
    /// </para>
    /// <para>
    /// **The time comes from two cached <c>dm_tran_commit_table</c> answers.** That DMV maps a commit
    /// sequence number to its commit time and a <c>SYS_CHANGE_VERSION</c> is one, so Change Tracking
    /// has the equivalent of <c>fn_cdc_map_lsn_to_time</c> after all — asked once for the mapping's
    /// version by the pass that stored it, and once per tick for the group's by the gate. Both
    /// present, and the figure is exact in the same sense CDC's is, in the same field.
    /// </para>
    /// <para>
    /// **The estimate is the fallback, and it is now a fallback rather than the routine path.** It
    /// covers exactly what a cache always has to cover: a <c>ChangeCheckHistory</c> row written
    /// before the gate started capturing this mechanism's time, a mapping that has not run since its
    /// column existed, and a capture that failed at either end. When it applies, the figure is
    /// reconstructed from this system's own polling — the first check that observed a version at or
    /// above the mapping's is the earliest moment we can say the source had reached where the mapping
    /// now is, and the distance from there to the latest check is how long it has been behind.
    /// Bounded above by the poll interval, reported under a name that says so.
    /// </para>
    /// </summary>
    private ReaderLag ChangeTrackingLag(
        string readerKind, ChangeSource source, AppliedPosition applied)
    {
        if (!long.TryParse(applied.Watermark, out var appliedVersion))
            return new ReaderLag(readerKind, Supported: true);

        var latest = checks.GetLatestCheck(
            source.ConnectionName, source.SourceDatabase, source.ReaderKind);

        // Never polled this group: both figures are measured against its latest check, and there
        // isn't one. A lag call is a read of a status screen, and the gate will have written a row
        // within a tick.
        if (latest is null)
            return new ReaderLag(readerKind, Supported: true);

        long? behind = latest.Value is not null && long.TryParse(latest.Value, out var current)
            ? Clamp(current - appliedVersion)
            : null;

        // Whichever end of this row the figure below turns out to rest on. The exact branch
        // subtracts from its SourceTimeUtc and the estimate from its CheckedAtUtc, so the same
        // coalesce that picks between them names the instant either figure is measured against.
        var asOf = latest.SourceTimeUtc ?? latest.CheckedAtUtc;

        if (ExactChangeTrackingLag(applied, latest) is { } exact)
            return new ReaderLag(
                readerKind, Supported: true, ExactLag: exact, ExactVersionsBehind: behind,
                AsOfUtc: asOf);

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
            EstimatedLag: crossing is null ? null : Clamp(latest.CheckedAtUtc - crossing.CheckedAtUtc),
            AsOfUtc: asOf);
    }

    /// <summary>
    /// The exact Change Tracking duration, or null to say one of the two commit times was never
    /// captured — which hands the figure to the estimate.
    /// <para>
    /// **Both ends come from the DMV, or neither is used.** Subtracting an engine-stated commit time
    /// from <c>CheckedAtUtc</c>, the wall-clock instant a poll happened to run, would be a hybrid
    /// whose error is the polling interval — exactly the error the estimate already owns and names —
    /// while landing in the field that promises there is no such error. Phase 85 established this
    /// when both lookups were live; it holds unchanged now that both are reads of a row.
    /// </para>
    /// <para>
    /// Nothing here can throw or block, so unlike phase 85 there is no unreachable-source case to
    /// catch: a source that was down when either value should have been captured simply left a null,
    /// and a null is already the condition this returns for.
    /// </para>
    /// </summary>
    private static TimeSpan? ExactChangeTrackingLag(AppliedPosition applied, ChangeCheck latest)
    {
        if (latest.Value is null || latest.SourceTimeUtc is null || applied.WatermarkTimeUtc is null)
            return null;

        return Clamp(latest.SourceTimeUtc.Value - applied.WatermarkTimeUtc.Value);
    }

    /// <summary>
    /// Negative is not a lag, it is a mapping that is ahead of the last thing we wrote down about its
    /// source — which happens legitimately, when a pass runs between two polls and reads past the
    /// position the earlier one recorded. Zero, because that mapping is not behind.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan lag) => lag < TimeSpan.Zero ? TimeSpan.Zero : lag;

    private static long Clamp(long behind) => behind < 0 ? 0 : behind;
}
