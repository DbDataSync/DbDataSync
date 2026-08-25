using DataSync.Core.Config;
using DataSync.TaskRunner;
using Xunit;

namespace DataSync.TaskRunner.Tests;

public sealed class WatermarkKeyTests
{
    [Fact]
    public void Build_CombinesConnectionDatabaseSchemaAndTable()
    {
        var source = new SourceTableRef { ConnectionName = "orders-db", Database = "App", Schema = "dbo", Table = "Orders" };

        Assert.Equal("orders-db/App/dbo.Orders", WatermarkKey.Build(source));
    }

    [Fact]
    public void Build_IsStableAcrossCalls()
    {
        var source = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "s", Table = "t" };

        Assert.Equal(WatermarkKey.Build(source), WatermarkKey.Build(source));
    }
}
