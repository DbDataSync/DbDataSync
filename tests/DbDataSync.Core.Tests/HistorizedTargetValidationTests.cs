using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A writer that appends history must not append it to the table it is reading. Each pass would grow
/// the source and the next would read what the last one wrote — and finding that out at run time
/// means finding out after the first pass has already doubled the table.
/// </summary>
public sealed class HistorizedTargetValidationTests
{
    private static SourceTableRef Source(string table = "Orders") => new()
    {
        ConnectionName = "src", Database = "App", Schema = "dbo", Table = table,
    };

    private static TableRef Target(string table = "Orders", string connection = "src", string schema = "dbo") => new()
    {
        ConnectionName = connection, Database = "App", Schema = schema, Table = table,
    };

    [Theory]
    [InlineData("Snapshot")]
    [InlineData("Scd2")]
    public void AHistorizingWriter_CannotWriteToItsOwnSource(string writer)
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateHistorizedTarget(writer, Source(), Target(), "orders"));

        Assert.Contains("appends history", problem.Message);
        Assert.Contains("different table", problem.Message);
    }

    /// <summary>Same connection is fine and needs nothing — it is the same *table object* that cannot
    /// work, which is the distinction the plan drew.</summary>
    [Fact]
    public void TheSameConnection_IsFineWithADifferentTable() =>
        ConfigValidation.ValidateHistorizedTarget("Scd2", Source(), Target("OrdersHistory"), "orders");

    [Fact]
    public void TheSameTableNameInAnotherSchema_IsFine() =>
        ConfigValidation.ValidateHistorizedTarget("Scd2", Source(), Target(schema: "history"), "orders");

    /// <summary>Every other writer replaces or upserts, so reading and writing one table is merely
    /// pointless rather than unbounded — and not this check's business.</summary>
    [Theory]
    [InlineData("MsSqlMerge")]
    [InlineData("DeleteInsert")]
    public void AnOrdinaryWriter_IsNotThisChecksBusiness(string writer) =>
        ConfigValidation.ValidateHistorizedTarget(writer, Source(), Target(), "orders");
}
