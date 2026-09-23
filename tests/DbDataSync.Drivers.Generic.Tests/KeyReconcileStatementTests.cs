using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>Phase 124's key-diff delete sweep — the reader's projection filter and both statements'
/// generated SQL, per dialect, with no server.</summary>
public sealed class KeyReconcileStatementTests
{
    private static readonly ColumnMapping Id = new() { SourceColumn = "Id", TargetColumn = "Id" };
    private static readonly ColumnMapping Region = new() { SourceColumn = "Region", TargetColumn = "Region" };
    private static readonly ColumnMapping Name = new() { SourceColumn = "Name", TargetColumn = "Name" };

    private static ColumnMetadata Key(string name) => new(name, "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: false);
    private static ColumnMetadata NonKey(string name) => new(name, "nvarchar", IsNullable: true, IsPrimaryKey: false, IsIdentity: false);

    [Fact]
    public void KeyColumnMappings_ASingleKey_ReturnsOnlyThatMapping()
    {
        var result = KeyReconcileReader.KeyColumnMappings(
            [Id, Region, Name], [Key("Id"), NonKey("Region"), NonKey("Name")], "orders");

        var mapping = Assert.Single(result);
        Assert.Equal("Id", mapping.SourceColumn);
    }

    [Fact]
    public void KeyColumnMappings_ACompositeKey_ReturnsBothInSourceOrder()
    {
        var result = KeyReconcileReader.KeyColumnMappings(
            [Id, Region, Name], [Key("Id"), Key("Region"), NonKey("Name")], "orders");

        Assert.Equal(["Id", "Region"], result.Select(m => m.SourceColumn));
    }

    [Fact]
    public void KeyColumnMappings_NoPrimaryKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyReconcileReader.KeyColumnMappings([Id, Name], [NonKey("Id"), NonKey("Name")], "orders"));

        Assert.Contains("no primary key", ex.Message);
        Assert.Contains("orders", ex.Message);
    }

    [Fact]
    public void KeyColumnMappings_AnUnmappedKeyColumn_Throws()
    {
        // "Region" is a key on the source, but nothing in columnMappings names it.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyReconcileReader.KeyColumnMappings([Id, Name], [Key("Id"), Key("Region"), NonKey("Name")], "orders"));

        Assert.Contains("Region", ex.Message);
        Assert.Contains("orders", ex.Message);
    }

    [Fact]
    public void Read_ProjectsOnlyTheKeyColumns_ComposingTheSegmentAndTheMappingsFilter()
    {
        var keyMappings = KeyReconcileReader.KeyColumnMappings(
            [Id, Region, Name], [Key("Id"), NonKey("Region"), NonKey("Name")], "orders");
        var projection = SourceProjection.Render(BracketDialect.Instance, keyMappings);

        Assert.Equal(
            """
            SELECT [Id] FROM [dbo].[Orders]
            WHERE [Id] >= @segMin AND [Id] < @segMax AND (Region = 'EU')
            """,
            KeyReconcileStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders",
                "[Id] >= @segMin AND [Id] < @segMax", "Region = 'EU'", projection));
    }

    [Fact]
    public void Read_WithACompositeKey_ProjectsBothColumns()
    {
        var keyMappings = KeyReconcileReader.KeyColumnMappings(
            [Id, Region, Name], [Key("Id"), Key("Region"), NonKey("Name")], "orders");
        var projection = SourceProjection.Render(BracketDialect.Instance, keyMappings);

        Assert.Equal(
            "SELECT [Id], [Region] FROM [dbo].[Orders]\nWHERE 1 = 1",
            KeyReconcileStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "1 = 1", null, projection));
    }

    [Fact]
    public void Range_AppliesTheFilter()
    {
        Assert.Equal(
            "SELECT MIN([Id]), MAX([Id]) FROM [dbo].[Orders] WHERE Region = 'EU'",
            KeyReconcileStatement.BuildRange(BracketDialect.Instance, "dbo", "Orders", "Id", "Region = 'EU'"));
    }

    [Fact]
    public void Count_ScopesToTheSegment()
    {
        Assert.Equal(
            "SELECT COUNT(*) FROM [dbo].[Orders] WHERE [Id] >= @segMin AND [Id] < @segMax",
            KeyReconcileDeleteStatement.BuildCount("[dbo].[Orders]", "[Id] >= @segMin AND [Id] < @segMax"));
    }

    [Fact]
    public void Delete_UsesACorrelatedNotExists_NotATupleNotIn()
    {
        Assert.Equal(
            """
            DELETE FROM [dbo].[Orders]
            WHERE [Id] >= @segMin AND [Id] < @segMax
              AND NOT EXISTS (SELECT 1 FROM [dbo].[DS_STG_x] s WHERE s.[Id] = [dbo].[Orders].[Id])
            """,
            KeyReconcileDeleteStatement.BuildDelete(
                BracketDialect.Instance, "[dbo].[Orders]", "[Id] >= @segMin AND [Id] < @segMax",
                "[dbo].[DS_STG_x]", ["Id"]));
    }

    [Fact]
    public void Delete_WithACompositeKey_JoinsOnEveryKeyColumn()
    {
        var sql = KeyReconcileDeleteStatement.BuildDelete(
            BracketDialect.Instance, "[dbo].[Orders]", "1 = 1", "[dbo].[DS_STG_x]", ["Id", "Region"]);

        Assert.Contains("s.[Id] = [dbo].[Orders].[Id] AND s.[Region] = [dbo].[Orders].[Region]", sql);
    }

    [Fact]
    public void Delete_FollowsTheDialectForQuoting()
    {
        var sql = KeyReconcileDeleteStatement.BuildDelete(ColonDialect.Instance, "t", "1 = 1", "s", ["Id"]);

        Assert.Contains("s.\"Id\" = t.\"Id\"", sql);
    }
}

/// <summary>Phase 129's SCD2 ending for the same key-diff sweep — closes an open version rather than
/// deleting the row, joined on the natural key rather than the target's real (surrogate) primary
/// key.</summary>
public sealed class KeyReconcileScd2CloseStatementTests
{
    [Fact]
    public void Count_ScopesToTheSegment_AndOnlyOpenRows()
    {
        Assert.Equal(
            "SELECT COUNT(*) FROM [dbo].[Orders] WHERE [DS_IsCurrent] = TRUE AND [Id] >= @segMin AND [Id] < @segMax",
            KeyReconcileScd2CloseStatement.BuildCount(
                BracketDialect.Instance, "[dbo].[Orders]", "[Id] >= @segMin AND [Id] < @segMax"));
    }

    [Fact]
    public void Close_UsesACorrelatedNotExists_NotATupleNotIn()
    {
        Assert.Equal(
            """
            UPDATE [dbo].[Orders]
            SET [DS_ValidTo] = @now,
                [DS_IsCurrent] = FALSE
            WHERE [DS_IsCurrent] = TRUE
              AND [Id] >= @segMin AND [Id] < @segMax
              AND NOT EXISTS (SELECT 1 FROM [dbo].[DS_STG_x] s WHERE s.[Id] = [dbo].[Orders].[Id])
            """,
            KeyReconcileScd2CloseStatement.BuildClose(
                BracketDialect.Instance, "[dbo].[Orders]", "[Id] >= @segMin AND [Id] < @segMax",
                "[dbo].[DS_STG_x]", ["Id"]));
    }

    [Fact]
    public void Close_JoinsOnTheNaturalKey_NotTheTargetsPrimaryKey()
    {
        // A composite natural key that differs entirely from whatever the target's own (surrogate)
        // primary key column is named — nothing about this statement ever mentions the surrogate.
        var sql = KeyReconcileScd2CloseStatement.BuildClose(
            BracketDialect.Instance, "[dbo].[OrdersHist]", "1 = 1", "[dbo].[DS_STG_x]", ["CustomerId", "Region"]);

        Assert.Contains(
            "s.[CustomerId] = [dbo].[OrdersHist].[CustomerId] AND s.[Region] = [dbo].[OrdersHist].[Region]", sql);
        Assert.DoesNotContain("DS_VersionKey", sql);
    }

    [Fact]
    public void Close_WithACompositeKey_JoinsOnEveryKeyColumn()
    {
        var sql = KeyReconcileScd2CloseStatement.BuildClose(
            BracketDialect.Instance, "[dbo].[Orders]", "1 = 1", "[dbo].[DS_STG_x]", ["Id", "Region"]);

        Assert.Contains("s.[Id] = [dbo].[Orders].[Id] AND s.[Region] = [dbo].[Orders].[Region]", sql);
    }

    [Fact]
    public void Close_FollowsTheDialectForQuoting()
    {
        var sql = KeyReconcileScd2CloseStatement.BuildClose(ColonDialect.Instance, "t", "1 = 1", "s", ["Id"]);

        Assert.Contains("s.\"Id\" = t.\"Id\"", sql);
        Assert.Contains(":now", sql);
    }

    [Fact]
    public void Count_FollowsTheDialectForQuoting()
    {
        var sql = KeyReconcileScd2CloseStatement.BuildCount(ColonDialect.Instance, "t", "1 = 1");

        Assert.Contains("\"DS_IsCurrent\"", sql);
    }
}
