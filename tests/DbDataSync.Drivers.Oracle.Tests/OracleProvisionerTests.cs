using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Oracle.Tests;

public sealed class OracleProvisionerTests
{
    private static readonly TableRef Table = new() { ConnectionName = "c", Database = "FREEPDB1", Schema = "SCHEMA", Table = "Orders" };

    [Fact]
    public async Task EnableSourceChangeCapture_ForWatermark_IsAlwaysSatisfiedWithZeroSteps()
    {
        var plan = await OracleProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest(
                ProvisioningActions.EnableSourceChangeCapture, Table, GenericDriverKinds.Watermark,
                new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    /// <summary>The one case where "Satisfied" still carries something an operator needs to read —
    /// Flashback needs no DDL, but it does need a grant no Oracle instance hands out by default, and
    /// that has to survive into the plan even though there is no step to attach it to.</summary>
    [Fact]
    public async Task EnableSourceChangeCapture_ForFlashback_IsSatisfiedButNamesItsRealCosts()
    {
        var plan = await OracleProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest(
                ProvisioningActions.EnableSourceChangeCapture, Table, OracleDriverKinds.Flashback,
                new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Warnings, w => w.Contains("DBMS_FLASHBACK"));
    }

    [Fact]
    public async Task AnUnknownAction_ReportsUnknownRatherThanThrowing()
    {
        var plan = await OracleProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest("somethingElse", Table, null, new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Unknown, plan.State);
        Assert.NotEmpty(plan.Warnings);
    }
}
