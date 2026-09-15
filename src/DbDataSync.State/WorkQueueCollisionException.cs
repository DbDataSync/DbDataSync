namespace DbDataSync.State;

/// <summary>
/// Thrown by <see cref="WorkQueueStore.EnqueueOrThrow"/> when the exact (task, kind, mapping, segment)
/// this call wanted to enqueue is already <c>Pending</c>/<c>Claimed</c>/<c>Running</c> under other,
/// unrelated in-flight work.
/// <para>
/// A distinct type — rather than <see cref="WorkQueueStore.Enqueue"/>'s own silent "return the existing
/// item's RunId instead" — because for some callers a collision is not the same request arriving twice
/// (where silently attaching to the winner is correct: two identical reload clicks should collapse into
/// one real reload, not run it twice — see <c>WorkQueueStore.Enqueue</c> itself, still used everywhere
/// that's true). For a mapping's own auto-triggered initial load racing an operator's independent manual
/// reload of the same segment, the two are different requests from different callers that happen to want
/// the same underlying work done; the loser's own caller needs to know its request did not happen the
/// way it thinks it did, not have it silently redirected to someone else's differently-scoped,
/// differently-timed work — see phase 143.
/// </para>
/// </summary>
/// <param name="taskName">The replication this collided within.</param>
/// <param name="runKind">The kind of work that collided.</param>
/// <param name="mappingName">The mapping this collided on.</param>
/// <param name="segmentLabel">The segment label that collided — <see cref="WorkQueueStore.NoSegment"/>
/// for an unsegmented unit of work.</param>
public sealed class WorkQueueCollisionException(
    string taskName, RunKind runKind, string mappingName, string segmentLabel)
    : Exception(
        $"A {runKind} is already in progress for mapping '{mappingName}' on '{taskName}'" +
        (segmentLabel == WorkQueueStore.NoSegment ? "" : $" (segment '{segmentLabel}')") +
        " — this will retry automatically once it finishes.")
{
    public string TaskName { get; } = taskName;
    public RunKind RunKind { get; } = runKind;
    public string MappingName { get; } = mappingName;
    public string SegmentLabel { get; } = segmentLabel;
}
