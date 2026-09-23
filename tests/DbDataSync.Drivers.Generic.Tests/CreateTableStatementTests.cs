using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>The generated <c>CREATE TABLE</c>'s shape — column order, nullability, and the primary key
/// clause — from already-rendered column types, independent of which dialect rendered them.</summary>
public sealed class CreateTableStatementTests
{
    private static RenderedColumnType Rendered(string sql) => new(sql, null);

    [Fact]
    public void EveryColumnIsRenderedWithItsNullabilityAndType()
    {
        var columns = new[]
        {
            new CreateTableColumn("Id", Rendered("int"), IsNullable: false, IsPrimaryKey: true),
            new CreateTableColumn("Name", Rendered("varchar(50)"), IsNullable: true, IsPrimaryKey: false),
        };

        // One column per line: a forty-column table on one line is unreadable wherever it is shown,
        // and formatting it here means every reader of the statement gets the same thing.
        Assert.Equal(
            """
            CREATE TABLE [dbo].[Orders] (
                [Id] int NOT NULL,
                [Name] varchar(50) NULL,
                PRIMARY KEY ([Id])
            )
            """.ReplaceLineEndings("\n").TrimEnd(),
            CreateTableStatement.Build(BracketDialect.Instance, "[dbo].[Orders]", columns));
    }

    [Fact]
    public void NoPrimaryKeyColumn_OmitsThePrimaryKeyClauseEntirely()
    {
        var columns = new[] { new CreateTableColumn("Name", Rendered("text"), IsNullable: true, IsPrimaryKey: false) };

        var sql = CreateTableStatement.Build(BracketDialect.Instance, "t", columns);

        Assert.DoesNotContain("PRIMARY KEY", sql);
    }

    [Fact]
    public void CompositePrimaryKey_ListsEveryKeyColumnInOrder()
    {
        var columns = new[]
        {
            new CreateTableColumn("TenantId", Rendered("int"), IsNullable: false, IsPrimaryKey: true),
            new CreateTableColumn("OrderId", Rendered("int"), IsNullable: false, IsPrimaryKey: true),
            new CreateTableColumn("Total", Rendered("decimal(18,2)"), IsNullable: true, IsPrimaryKey: false),
        };

        var sql = CreateTableStatement.Build(BracketDialect.Instance, "t", columns);

        Assert.Contains("PRIMARY KEY ([TenantId], [OrderId])", sql);
    }

    [Fact]
    public void AColumnNamedToBreakOutOfItsQuoting_IsEscapedByTheDialect()
    {
        var columns = new[] { new CreateTableColumn("Order]Id", Rendered("int"), IsNullable: false, IsPrimaryKey: true) };

        var sql = CreateTableStatement.Build(BracketDialect.Instance, "t", columns);

        Assert.Contains("[Order]]Id]", sql);
    }

    [Fact]
    public void FollowsTheDialectForQuoting()
    {
        var columns = new[] { new CreateTableColumn("Id", Rendered("int4"), IsNullable: false, IsPrimaryKey: true) };

        Assert.Equal(
            """
            CREATE TABLE t (
                "Id" int4 NOT NULL,
                PRIMARY KEY ("Id")
            )
            """.ReplaceLineEndings("\n").TrimEnd(),
            CreateTableStatement.Build(ColonDialect.Instance, "t", columns));
    }

    [Fact]
    public void NoColumns_Throws() =>
        Assert.Throws<ArgumentException>(() => CreateTableStatement.Build(BracketDialect.Instance, "t", []));
}
