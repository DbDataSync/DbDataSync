using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// <see cref="MsSqlProvisioner.BuildEnableChangeCaptureSteps"/> in isolation, against plain booleans
/// rather than a live server — see phase 25's "MsSqlProvisioner plan shapes, against faked catalog
/// results" in its verification plan.
/// </summary>
public sealed class MsSqlProvisionerTests
{
    private const string Database = "Sales";
    private const string QualifiedTable = "[dbo].[Orders]";

    private static ProvisioningPlan Plan(
        bool hasPrimaryKey = true,
        bool databaseLevel = false,
        bool tableLevel = false,
        bool snapshotRequested = false,
        bool? snapshotEnabled = null,
        int retentionDays = 7) =>
        MsSqlProvisioner.BuildEnableChangeCaptureSteps(
            Database, QualifiedTable, hasPrimaryKey, databaseLevel, tableLevel, snapshotRequested, snapshotEnabled, retentionDays);

    [Fact]
    public void NoPrimaryKey_PlansUnsupported_NamingTheTable()
    {
        var plan = Plan(hasPrimaryKey: false);

        Assert.Equal(ProvisioningState.Unsupported, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains(QualifiedTable));
    }

    [Fact]
    public void AlreadyEnabledAtBothLevels_IsSatisfiedWithZeroSteps()
    {
        var plan = Plan(databaseLevel: true, tableLevel: true);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void DatabaseOnTableOff_PlansExactlyOneAlterTableStep()
    {
        var plan = Plan(databaseLevel: true, tableLevel: false);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(ProvisioningStepScope.Table, step.Scope);
        Assert.Contains("ENABLE CHANGE_TRACKING", step.CommandText);
        Assert.Contains(QualifiedTable, step.CommandText);
    }

    [Fact]
    public void DatabaseOffTableOff_PlansTheDatabaseStepBeforeTheTableStep()
    {
        var plan = Plan(databaseLevel: false, tableLevel: false);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(ProvisioningStepScope.Database, plan.Steps[0].Scope);
        Assert.Contains($"ALTER DATABASE [{Database}] SET CHANGE_TRACKING = ON", plan.Steps[0].CommandText);
        Assert.Contains("CHANGE_RETENTION = 7 DAYS", plan.Steps[0].CommandText);
        Assert.Equal(ProvisioningStepScope.Table, plan.Steps[1].Scope);
    }

    [Fact]
    public void RetentionDays_FlowsIntoTheGeneratedStatement()
    {
        var plan = Plan(databaseLevel: false, retentionDays: 3);

        Assert.Contains("CHANGE_RETENTION = 3 DAYS", plan.Steps[0].CommandText);
    }

    [Fact]
    public void SnapshotIsolationNotRequested_NeverPlansTheAllowSnapshotIsolationStep()
    {
        var plan = Plan(databaseLevel: true, tableLevel: true, snapshotRequested: false, snapshotEnabled: false);

        Assert.DoesNotContain(plan.Steps, s => s.CommandText.Contains("ALLOW_SNAPSHOT_ISOLATION"));
    }

    [Fact]
    public void SnapshotIsolationRequestedAndAlreadyOn_DoesNotRepeatTheStep()
    {
        var plan = Plan(databaseLevel: true, tableLevel: true, snapshotRequested: true, snapshotEnabled: true);

        Assert.Empty(plan.Steps);
        Assert.Equal(ProvisioningState.Satisfied, plan.State);
    }

    [Fact]
    public void SnapshotIsolationRequestedAndOff_PlansTheAllowSnapshotIsolationStep()
    {
        var plan = Plan(databaseLevel: true, tableLevel: true, snapshotRequested: true, snapshotEnabled: false);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(ProvisioningStepScope.Database, step.Scope);
        Assert.Contains($"ALTER DATABASE [{Database}] SET ALLOW_SNAPSHOT_ISOLATION ON", step.CommandText);
    }

    [Fact]
    public void EverythingMissing_PlansAllThreeStepsInOrder()
    {
        var plan = Plan(databaseLevel: false, tableLevel: false, snapshotRequested: true, snapshotEnabled: false);

        Assert.Equal(3, plan.Steps.Count);
        Assert.Contains("CHANGE_TRACKING = ON", plan.Steps[0].CommandText);
        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION", plan.Steps[1].CommandText);
        Assert.Contains("ENABLE CHANGE_TRACKING", plan.Steps[2].CommandText);
    }

    [Theory]
    [InlineData(DataSync.Drivers.Generic.GenericDriverKinds.Watermark)]
    [InlineData(DataSync.Drivers.Generic.GenericDriverKinds.BatchReload)]
    [InlineData(MsSqlDriverKinds.BatchReload)]
    public async Task AReaderKindNeedingNoSourceCooperation_IsSatisfiedWithoutTouchingTheConnection(string readerKind)
    {
        // No connection is passed — if this reached a catalog query it would NullReferenceException,
        // which is exactly what proves the early return never gets there.
        var plan = await MsSqlProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest(
                ProvisioningActions.EnableSourceChangeCapture, Table(), readerKind, new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task AnUnknownAction_ReportsUnknownRatherThanThrowing()
    {
        var plan = await MsSqlProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest("somethingElse", Table(), null, new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Unknown, plan.State);
        Assert.NotEmpty(plan.Warnings);
    }

    private static DataSync.Core.Config.TableRef Table() =>
        new() { ConnectionName = "c", Database = Database, Schema = "dbo", Table = "Orders" };
}
