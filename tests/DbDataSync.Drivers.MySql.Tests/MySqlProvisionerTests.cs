using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MySql.Tests;

public sealed class MySqlProvisionerTests
{
    private static readonly TableRef Table = new() { ConnectionName = "c", Database = "db", Schema = "db", Table = "Orders" };

    [Fact]
    public async Task EnableSourceChangeCapture_ForWatermark_IsAlwaysSatisfiedWithZeroSteps()
    {
        // Neither generic reader (Watermark, BatchReload) needs any source cooperation, so this never
        // even has to look at a connection.
        var plan = await MySqlProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest(
                ProvisioningActions.EnableSourceChangeCapture, Table, GenericDriverKinds.Watermark,
                new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task AnUnknownAction_ReportsUnknownRatherThanThrowing()
    {
        var plan = await MySqlProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest("somethingElse", Table, null, new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Unknown, plan.State);
        Assert.NotEmpty(plan.Warnings);
    }
}
