namespace DbDataSync.State.Tests;

/// <summary>
/// Which <see cref="RunKind"/>s belong to which <see cref="RunLane"/> — phase 108, renamed by phase
/// 133. Kept two-valued (<c>ChangeProcessing</c>/<c>BulkLoad</c>) rather than split further, despite
/// <c>Verification</c> and <c>ReconcileDeletes</c> also riding the <c>BulkLoad</c> lane.
/// </summary>
public sealed class RunLanesTests
{
    [Fact]
    public void PrimaryRoutesToTheChangeProcessingLane()
    {
        Assert.Equal(RunLane.ChangeProcessing, RunLanes.LaneFor(RunKind.Primary));
        Assert.Equal([RunKind.Primary], RunLanes.KindsFor(RunLane.ChangeProcessing));
    }

    [Fact]
    public void BulkLoadRoutesToTheBulkLoadLane_PairedWithVerificationAndReconcileDeletes()
    {
        Assert.Equal(RunLane.BulkLoad, RunLanes.LaneFor(RunKind.BulkLoad));
        Assert.Equal(RunLane.BulkLoad, RunLanes.LaneFor(RunKind.Verification));
        Assert.Equal(RunLane.BulkLoad, RunLanes.LaneFor(RunKind.ReconcileDeletes));

        Assert.Equal(
            [RunKind.BulkLoad, RunKind.Verification, RunKind.ReconcileDeletes],
            RunLanes.KindsFor(RunLane.BulkLoad));
    }
}
