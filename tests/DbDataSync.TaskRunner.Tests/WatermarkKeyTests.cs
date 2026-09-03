using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.TaskRunner;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

public sealed class WatermarkKeyTests
{
    private static readonly SqlDialect MsSql = MsSqlDialect.Instance;
    private static readonly SqlDialect Postgres = PostgresDialect.Instance;

    [Fact]
    public void Build_CombinesConnectionDatabaseSchemaAndTable()
    {
        var source = new SourceTableRef { ConnectionName = "orders-db", Database = "App", Schema = "dbo", Table = "Orders" };

        Assert.Equal("orders-db/[App]/[dbo].[Orders]", WatermarkKey.Build(source, MsSql));
    }

    [Fact]
    public void Build_IsStableAcrossCalls()
    {
        var source = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "s", Table = "t" };

        Assert.Equal(WatermarkKey.Build(source, MsSql), WatermarkKey.Build(source, MsSql));
    }

    /// <summary>
    /// The collision the old interpolated format could not tell apart: schema <c>a.b</c> table
    /// <c>c</c> and schema <c>a</c> table <c>b.c</c> both produced <c>conn/db/a.b.c</c>. Quoting is
    /// what makes them two strings, and it is the reason this phase's migration exists.
    /// </summary>
    [Fact]
    public void Build_DistinguishesADottedSchemaFromADottedTable()
    {
        var dottedSchema = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "a.b", Table = "c" };
        var dottedTable = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "a", Table = "b.c" };

        Assert.NotEqual(WatermarkKey.Build(dottedSchema, MsSql), WatermarkKey.Build(dottedTable, MsSql));
        Assert.NotEqual(WatermarkKey.Build(dottedSchema, Postgres), WatermarkKey.Build(dottedTable, Postgres));
    }

    /// <summary>The database is an identifier too, and a slash in one used to be the same ambiguity a
    /// segment further left.</summary>
    [Fact]
    public void Build_DistinguishesASlashInTheDatabaseFromTheSeparatorBeforeIt()
    {
        var slashedDatabase = new SourceTableRef { ConnectionName = "a", Database = "b/c", Schema = "s", Table = "t" };
        var slashedConnection = new SourceTableRef { ConnectionName = "a/b", Database = "c", Schema = "s", Table = "t" };

        Assert.NotEqual(WatermarkKey.Build(slashedDatabase, MsSql), WatermarkKey.Build(slashedConnection, MsSql));
    }

    /// <summary>
    /// A name carrying the dialect's own closing delimiter cannot end the identifier early — the
    /// property that makes "unambiguous by construction" true rather than merely likely.
    /// </summary>
    [Fact]
    public void Build_EscapesAClosingDelimiterInsideAName()
    {
        var awkward = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "dbo", Table = "Or]ders" };
        var plain = new SourceTableRef { ConnectionName = "c", Database = "d", Schema = "dbo", Table = "Orders" };

        Assert.NotEqual(WatermarkKey.Build(awkward, MsSql), WatermarkKey.Build(plain, MsSql));
        Assert.Equal("c/[d]/[dbo].[Or]]ders]", WatermarkKey.Build(awkward, MsSql));
    }
}
