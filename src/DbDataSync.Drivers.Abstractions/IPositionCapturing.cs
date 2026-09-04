using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// A reader that can report its current position **without reading a single row** — the capability a
/// safe handover to a Bulk Load pipeline depends on.
/// <para>
/// This is what phase 101's §1 table's <c>InitialLoad</c> column became once
/// architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md retargeted it: an initial
/// load is only correct if the change feed's position is captured *before* the table is read —
/// <c>capture the source position → run the Bulk Load → persist the position → switch the intent to
/// Changes</c> — or a change made during a multi-hour load is lost silently. That capture cannot be a
/// read of the table itself, or the two race.
/// </para>
/// <para>
/// Opt-in, in the same shape as <see cref="IPositionAcknowledging"/> and
/// <see cref="ISegmentExpandingReader"/>: a reader that has no honest answer (a reload reader, whose
/// only "position" is the previous one echoed back; a scripted or DuckDB query, whose behaviour is
/// per-script) does not implement this at all, rather than throwing from it. Most readers that do
/// implement it already compute exactly this value as part of an ordinary pass — Change Tracking's
/// current-version lookup is already public and already reused by the change-polling gate's counter
/// source, and the Watermark reader's own <c>MAX(column)</c> statement is its equivalent — so this
/// exposes what already exists through <see cref="IChangeReader"/> rather than duplicating it.
/// </para>
/// <para>
/// **Nothing calls this yet.** The Bulk Load pipeline that will is future, unscheduled work (see the
/// bulk-load doc's Phase B) — this interface only has to be declared and correctly implemented ahead of
/// it, so the capability is honestly expressed and ready for whoever builds that pipeline next. See
/// architecture/implementation/done/phase-101-readers-honour-the-read-intent.md's amended §1.
/// </para>
/// </summary>
public interface IPositionCapturing
{
    /// <summary>
    /// This reader's current position, and the time the source puts on it where the mechanism can say
    /// (null where it cannot, or has none to say) — in the same text encoding <c>ChangeWatermarks</c>
    /// stores a watermark in, so the result can be persisted exactly as any other position is.
    /// </summary>
    Task<CapturedPosition> CapturePositionAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}

/// <summary>
/// One reader's answer to <see cref="IPositionCapturing.CapturePositionAsync"/>.
/// </summary>
/// <param name="Position">The current position, in the same text encoding a stored watermark uses for
/// this reader.</param>
/// <param name="PositionTimeUtc">When the source says <paramref name="Position"/> committed, or null
/// when this mechanism has no such mapping, or the engine declined to place it.</param>
public sealed record CapturedPosition(string Position, DateTimeOffset? PositionTimeUtc);
