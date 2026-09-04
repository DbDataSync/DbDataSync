using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Opt-in capability, in the same shape as <see cref="ISegmentExpandingReader"/> and
/// <see cref="IPositionAcknowledging"/>: a reader that can honestly say which <see cref="ReadIntent"/>
/// values it can honour implements this; one whose answer is "whatever the caller handed it decides"
/// (a scripted query, whose behaviour is per-script rather than per-Kind) does not, and says so where
/// it is registered instead. See
/// architecture/implementation/done/phase-101-readers-honour-the-read-intent.md.
/// <para>
/// **The declaration is not decoration.** It is what a run-time pass is allowed to ask this reader to
/// do — <c>RunExecutor</c> refuses an intent that is not in this set rather than silently reading the
/// whole table, which is the guarantee phase 101 exists to give — and, once phase 102 builds it, what
/// the Monitoring tab offers as a choice.
/// </para>
/// <para>
/// **<see cref="ReadIntent.InitialLoad"/> is never in this set**, for any reader.
/// architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md retargeted this mid-phase:
/// once a Bulk Load pipeline performs an initial load (future, unscheduled work) rather than the reader
/// itself, every mapping can request one regardless of its reader — a per-reader question with only one
/// possible answer, so it stops being asked here at all. <c>RunExecutor</c> never refuses
/// <c>InitialLoad</c>, whether or not a reader implements this interface. What a reader can still
/// honestly decline is <c>Changes</c>/<c>ChangesFromEarliest</c>/<c>ChangesFromLatest</c>, which remain
/// genuinely per-reader — see <see cref="IPositionCapturing"/> for the capability that replaced
/// <c>InitialLoad</c>'s old column in this interface's table.
/// </para>
/// </summary>
public interface IReadIntentDeclaring
{
    /// <summary>
    /// Every <see cref="ReadIntent"/> this reader can honestly carry out, drawn only from
    /// <see cref="ReadIntent.Changes"/>, <see cref="ReadIntent.ChangesFromEarliest"/> and
    /// <see cref="ReadIntent.ChangesFromLatest"/> — never <see cref="ReadIntent.InitialLoad"/>, and never
    /// empty for a reader that implements this at all. A reader with nothing to say about any of the
    /// three should not implement the interface, the same way <see cref="ISegmentExpandingReader"/> is
    /// simply absent from a reader that cannot expand a segment rather than present and throwing.
    /// </summary>
    IReadOnlySet<ReadIntent> SupportedIntents { get; }
}
