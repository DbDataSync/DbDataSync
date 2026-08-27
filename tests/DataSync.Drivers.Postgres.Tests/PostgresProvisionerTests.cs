using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Postgres.Tests;

public sealed class PostgresProvisionerTests
{
    private static readonly TableRef Table = new() { ConnectionName = "c", Database = "db", Schema = "dbo", Table = "Orders" };

    [Fact]
    public async Task EnableSourceChangeCapture_IsAlwaysSatisfiedWithZeroSteps()
    {
        // Neither generic reader (Watermark, BatchReload) needs any source cooperation, so this never
        // even has to look at a connection — the Postgres driver registers no reader that would.
        var plan = await PostgresProvisioner.PlanAsync(
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
        var plan = await PostgresProvisioner.PlanAsync(
            connection: null!,
            new ProvisioningRequest("somethingElse", Table, null, new Dictionary<string, string>(), []),
            CancellationToken.None);

        Assert.Equal(ProvisioningState.Unknown, plan.State);
        Assert.NotEmpty(plan.Warnings);
    }
}
