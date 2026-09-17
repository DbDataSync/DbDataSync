namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>The reader's and writer's generated SQL, per dialect, with no server.</summary>
public sealed class PipelineStatementTests
{
    [Fact]
    public void BatchRead_ComposesTheSegmentPredicateWithTheMappingsOwnFilter()
    {
        // A segment narrows a reload *within* the subset the mapping was always scoped to. Replacing
        // the filter rather than composing with it would make a segmented reload read — and a
        // reconciling writer then delete — rows the mapping never owned.
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE [OrderId] >= @segMin AND [OrderId] < @segMax AND (Region = 'EU');
            """,
            BatchReloadStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders",
                "[OrderId] >= @segMin AND [OrderId] < @segMax", "Region = 'EU'"));
    }

    [Fact]
    public void BatchRead_WithNoFilter_IsJustTheScope()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1;
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "1 = 1", filter: null));
    }

    [Fact]
    public void Range_AppliesTheFilterSoAutoBucketsCoverOnlyTheMappedSubset()
    {
        Assert.Equal(
            "SELECT MIN([OrderId]), MAX([OrderId]) FROM [dbo].[Orders] WHERE Region = 'EU';",
            BatchReloadStatement.BuildRange(BracketDialect.Instance, "dbo", "Orders", "OrderId", "Region = 'EU'"));
    }

    [Fact]
    public void Delete_ScopesToTheSegmentRatherThanTheWholeTable()
    {
        Assert.Equal(
            "DELETE FROM [dbo].[Orders] WHERE [OrderId] >= @segMin AND [OrderId] < @segMax;",
            DeleteInsertStatement.BuildDelete("[dbo].[Orders]", "[OrderId] >= @segMin AND [OrderId] < @segMax"));
    }

    [Fact]
    public void Insert_SkipsStagedDeletes()
    {
        // The delete already removed everything in scope, so re-inserting a 'D' row would be adding a
        // row back in order to record that it isn't there.
        Assert.Equal(
            """
            INSERT INTO [dbo].[Orders] ([Id], [Name])
            SELECT [Id], [Name] FROM [dbo].[DS_STG_x]
            WHERE [__Operation] <> 'D';
            """,
            DeleteInsertStatement.BuildInsert(BracketDialect.Instance, "[dbo].[Orders]", "[Id], [Name]", "[dbo].[DS_STG_x]"));
    }

    [Fact]
    public void Insert_FollowsTheDialectForTheMarkerColumnsQuoting()
    {
        Assert.Contains("\"__Operation\" <> 'D'",
            DeleteInsertStatement.BuildInsert(ColonDialect.Instance, "t", "c", "s"));
    }

    [Fact]
    public void Insert_Chunked_RefillsOneOrdinalRangeAtATime()
    {
        Assert.Equal(
            """
            INSERT INTO [dbo].[Orders] ([Id], [Name])
            SELECT [Id], [Name] FROM [dbo].[DS_STG_x]
            WHERE [__Operation] <> 'D'
              AND [__Ordinal] > @afterOrdinal
              AND [__Ordinal] <= @upToOrdinal;
            """,
            DeleteInsertStatement.BuildInsert(
                BracketDialect.Instance, "[dbo].[Orders]", "[Id], [Name]", "[dbo].[DS_STG_x]",
                overrideGenerated: false, chunked: true));
    }

    [Fact]
    public void Insert_Chunked_KeepsTheStagedDeleteFilter()
    {
        // The bound narrows which staged rows this statement carries; it must not quietly widen what
        // they mean. A 'D' row inside the range is still not something to insert.
        Assert.Contains("[__Operation] <> 'D'",
            DeleteInsertStatement.BuildInsert(
                BracketDialect.Instance, "t", "c", "s", overrideGenerated: false, chunked: true));
    }

    [Fact]
    public void Insert_Chunked_FollowsTheDialectForPlaceholders()
    {
        Assert.Contains(":afterOrdinal",
            DeleteInsertStatement.BuildInsert(
                ColonDialect.Instance, "t", "c", "s", overrideGenerated: false, chunked: true));
    }
}
