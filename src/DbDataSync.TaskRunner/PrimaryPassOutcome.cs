using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.TaskRunner;

/// <summary>
/// What a completed <c>Primary</c> pass does to a mapping's stored cursor — split out of
/// <see cref="RunExecutor"/> so the rule ("every intent transitions to <see cref="ReadIntent.Changes"/>
/// once a pass applies its changes, written in the same place and under the same conditions the
/// watermark already is") can be pinned directly against the state store, without a real source or
/// target connection. See architecture/implementation/done/phase-101-readers-honour-the-read-intent.md.
/// </summary>
public static class PrimaryPassOutcome
{
    /// <summary>
    /// Called once per <c>Primary</c> pass, after the target write has committed — never before, and
    /// never for a Backfill or a Verification. A pass that read nothing new
    /// (<paramref name="newWatermark"/> null) changes nothing here: the watermark stays where it was
    /// and so does the intent, for the same reason a failed pass leaves both alone — there is nothing
    /// new to record.
    /// </summary>
    /// <returns>
    /// The advance this pass made durable, for <c>TaskRuns</c>' own history (phase 71) — or null when
    /// there was none.
    /// </returns>
    public static WatermarkChange? Apply(
        IRunnerState state,
        string taskName,
        string mappingName,
        string watermarkKey,
        string? previousWatermark,
        string? newWatermark,
        DateTimeOffset? newWatermarkTimeUtc)
    {
        if (newWatermark is null)
            return null;

        state.SetWatermark(taskName, mappingName, watermarkKey, newWatermark, newWatermarkTimeUtc);

        // Unconditional, not "only if it was not already Changes": InitialLoad, ChangesFromEarliest and
        // ChangesFromLatest all transition here, and a mapping already at Changes staying at Changes is
        // the same write with no visible effect — cheaper to make than to special-case around.
        state.SetReadIntent(taskName, mappingName, watermarkKey, ReadIntent.Changes);

        return new WatermarkChange(previousWatermark, newWatermark);
    }
}

/// <summary>
/// A watermark advance one pass actually made durable, for the history on <c>TaskRuns</c> (phase 71).
/// One nullable value rather than two loose strings, so "this run moved the watermark" is a single
/// question with a single answer — see phase 71's original reasoning on <c>RunExecutor</c>'s own copy
/// of this record, which this replaces as the shared, testable home for the same fact.
/// </summary>
public sealed record WatermarkChange(string? Previous, string New);
