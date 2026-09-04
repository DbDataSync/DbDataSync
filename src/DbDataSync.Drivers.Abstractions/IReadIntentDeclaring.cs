using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Opt-in capability, in the same shape as <see cref="ISegmentExpandingReader"/> and
/// <see cref="IPositionAcknowledging"/>: a reader that can honestly say which <see cref="ReadIntent"/>
/// values it can honour implements this; one whose answer is "whatever the caller handed it decides"
/// (a scripted query, whose behaviour is per-script rather than per-Kind) does not, and says so where
/// it is registered instead. See
/// architecture/implementation/todo/phase-101-readers-honour-the-read-intent.md.
/// <para>
/// **The declaration is not decoration.** It is what a run-time pass is allowed to ask this reader to
/// do — <c>RunExecutor</c> refuses an intent that is not in this set rather than silently reading the
/// whole table, which is the guarantee phase 101 exists to give — and, once phase 102 builds it, what
/// the Monitoring tab offers as a choice.
/// </para>
/// </summary>
public interface IReadIntentDeclaring
{
    /// <summary>
    /// Every <see cref="ReadIntent"/> this reader can honestly carry out. Never empty for a reader that
    /// implements this at all — a reader with nothing to say about any intent should not implement the
    /// interface, the same way <see cref="ISegmentExpandingReader"/> is simply absent from a reader that
    /// cannot expand a segment rather than present and throwing.
    /// </summary>
    IReadOnlySet<ReadIntent> SupportedIntents { get; }
}
