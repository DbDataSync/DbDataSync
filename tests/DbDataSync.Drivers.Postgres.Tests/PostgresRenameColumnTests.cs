using DbDataSync.Drivers.Postgres;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>Postgres keeps the ANSI spelling of a column rename — the divergence is SQL Server's,
/// which has no such statement at all, not the default's.</summary>
public sealed class PostgresRenameColumnTests
{
    [Fact]
    public void ARenameIsAnAnsiAlterTable()
    {
        var sql = PostgresDialect.Instance.RenderRenameColumn("\"public\".\"orders\"", "cust_id", "customer_id");

        Assert.Equal("ALTER TABLE \"public\".\"orders\" RENAME COLUMN \"cust_id\" TO \"customer_id\";", sql);
    }
}
