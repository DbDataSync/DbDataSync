using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// A reader that wants to be told when a position has been **durably** reached, so the source can
/// stop keeping the history behind it.
/// <para>
/// Opt-in by interface, like <see cref="ISegmentExpandingReader"/> and <c>IConnectionTester</c>: most
/// readers have nothing to acknowledge. A trigger-maintained shadow table grows forever unless
/// something deletes below the watermark, and the watermark lives in DbDataSync's state store rather
/// than in the source — so something has to carry it back. Postgres replication slots
/// (<c>pg_replication_slot_advance</c>) and MySQL's binlog pruning need the same call, which is why
/// this is on the abstraction rather than inside one reader.
/// </para>
/// <para>
/// **Called after the write commits and after the watermark is persisted, and never before.** The
/// watermark-on-success-only rule is what makes a failed run safe to retry; acknowledging early would
/// let the source discard changes this replication has not actually written, turning a retryable
/// failure into permanent data loss. Acknowledgement failing is logged and does not fail the run —
/// the data is already at the target, and unpruned history is a disk-space problem rather than a
/// correctness one.
/// </para>
/// </summary>
public interface IPositionAcknowledging
{
    Task AcknowledgeAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string watermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
