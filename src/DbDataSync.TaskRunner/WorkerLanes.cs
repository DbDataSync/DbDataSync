namespace DbDataSync.TaskRunner;

/// <summary>
/// The degree of parallelism for each of a worker's two lanes — <c>ChangeProcessing</c> drains
/// <c>RunKind.Primary</c>, <c>BulkLoad</c> drains <c>RunKind.BulkLoad</c> and
/// <c>RunKind.Verification</c>. Each lane has its own bounded channel and its own pool of consumers,
/// so a long reload can never take a slot an incremental pass needs. See
/// <c>DbDataSync.Core.Config.ChangeProcessingConfig</c> and phase-108.
/// </summary>
public sealed record WorkerLanes(int ChangeProcessing, int BulkLoad)
{
    /// <summary>Same value for both lanes — the shape tests want when the split is not what they are
    /// asserting.</summary>
    public static WorkerLanes Uniform(int degreeOfParallelism) =>
        new(degreeOfParallelism, degreeOfParallelism);
}
