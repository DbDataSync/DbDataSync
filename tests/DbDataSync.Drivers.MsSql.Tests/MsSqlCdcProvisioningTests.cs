using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>What enabling CDC on a table plans, with no connection involved — the same shape the
/// Change Tracking enablement tests take, and for the same reason.</summary>
public sealed class MsSqlCdcProvisioningTests
{
    private static ProvisioningPlan Plan(
        bool hasPrimaryKey = true, bool cdcEnabledAtDatabase = true, CdcCaptureInstance? instance = null) =>
        MsSqlProvisioner.BuildEnableCdcSteps("App", "dbo", "Orders", hasPrimaryKey, cdcEnabledAtDatabase, instance);

    [Fact]
    public void AFreshDatabase_EnablesCdcThenTheTable()
    {
        var plan = Plan(cdcEnabledAtDatabase: false);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains("sp_cdc_enable_db", plan.Steps[0].CommandText);
        Assert.Contains("sp_cdc_enable_table", plan.Steps[1].CommandText);
    }

    /// <summary>Net changes is what makes CDC usable for mirroring — one row per key rather than every
    /// intermediate change — so it is asked for whenever the table can support it.</summary>
    [Fact]
    public void ATableWithAKey_AsksForNetChanges() =>
        Assert.Contains("@supports_net_changes = 1", Assert.Single(Plan().Steps).CommandText);

    /// <summary>
    /// No key means no net changes, and that is a working configuration rather than a broken one — so
    /// it is enabled anyway, with a warning, instead of being refused the way Change Tracking is.
    /// </summary>
    [Fact]
    public void ATableWithNoKey_IsStillEnabled_AndSaysWhatItLoses()
    {
        var plan = Plan(hasPrimaryKey: false);

        Assert.Equal(ProvisioningState.Missing, plan.State);
        Assert.Contains("@supports_net_changes = 0", Assert.Single(plan.Steps).CommandText);
        Assert.Contains("no primary key", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void AnAlreadyCapturedTable_HasNothingToDo()
    {
        var plan = Plan(instance: new CdcCaptureInstance("dbo_Orders", SupportsNetChanges: true, ["Id"]));

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
    }

    /// <summary>
    /// Changing an existing instance to support net changes means dropping and recreating it, which
    /// discards captured history. That is a decision an operator makes, not a repair a plan performs.
    /// </summary>
    [Fact]
    public void AnInstanceWithoutNetChanges_IsReportedRatherThanRecreated()
    {
        var plan = Plan(instance: new CdcCaptureInstance("dbo_Orders", SupportsNetChanges: false, ["Id"]));

        Assert.Equal(ProvisioningState.Satisfied, plan.State);
        Assert.Empty(plan.Steps);
        Assert.Contains("discards the history", Assert.Single(plan.Warnings));
    }

    /// <summary>A schema or table name is a string literal to sp_cdc_enable_table, not an identifier,
    /// so a quote in one would end the literal and leave the rest as SQL.</summary>
    [Fact]
    public void AQuoteInAName_IsEscapedForTheLiteral()
    {
        var plan = MsSqlProvisioner.BuildEnableCdcSteps(
            "App", "dbo", "O'Brien", hasPrimaryKey: true, cdcEnabledAtDatabase: true, existingInstance: null);

        Assert.Contains("N'O''Brien'", Assert.Single(plan.Steps).CommandText);
    }
}
