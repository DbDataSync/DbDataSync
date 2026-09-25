using DbDataSync.Core.Config;

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
            WHERE [OrderId] >= @segMin AND [OrderId] < @segMax AND (Region = 'EU')
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null,
                "[OrderId] >= @segMin AND [OrderId] < @segMax", "Region = 'EU'"));
    }

    [Fact]
    public void BatchRead_WithNoFilter_IsJustTheScope()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "1 = 1", filter: null));
    }

    private static RelationshipConfig Rel(string name, string table, params (string Local, string Foreign)[] joinKeys) =>
        new()
        {
            Name = name,
            Table = table,
            JoinKeys = joinKeys.Select(k => new RelationshipJoinKey { LocalColumn = k.Local, ForeignColumn = k.Foreign }).ToList(),
        };

    [Fact]
    public void BatchRead_WithARelationship_JoinsAndAliasesThePrimaryTable()
    {
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            """
            SELECT [Id],
                r0.[Name] FROM [dbo].[Orders] AS base
            LEFT JOIN [dbo].[Region] AS r0 ON base.[RegionId] = r0.[Id]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "1 = 1", filter: null,
                projection: "[Id],\n    r0.[Name]", relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void BatchRead_WithMultipleJoinKeys_AndsThemTogether()
    {
        var relationships = new List<RelationshipConfig>
        {
            Rel("region", "Region", ("CountryCode", "CountryCode"), ("RegionId", "Id")),
        };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders] AS base
            LEFT JOIN [dbo].[Region] AS r0 ON base.[CountryCode] = r0.[CountryCode] AND base.[RegionId] = r0.[Id]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "1 = 1", filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void BatchRead_WithMultipleRelationships_AliasesEachR0R1()
    {
        var relationships = new List<RelationshipConfig>
        {
            Rel("region", "Region", ("RegionId", "Id")),
            Rel("manager", "Employee", ("ManagerId", "Id")),
        };
        var aliases = new Dictionary<string, string> { ["region"] = "r0", ["manager"] = "r1" };

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders] AS base
            LEFT JOIN [dbo].[Region] AS r0 ON base.[RegionId] = r0.[Id]
            LEFT JOIN [dbo].[Employee] AS r1 ON base.[ManagerId] = r1.[Id]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "1 = 1", filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void BatchRead_ARelationshipDeclaredButNotReferenced_RendersNoJoinAtAll()
    {
        // Assign leaves an unmapped relationship out of the alias dictionary entirely — this is what
        // that looks like downstream: the declared relationship exists in `relationships`, but with no
        // alias assigned for it, no JOIN is rendered — same statement as no relationships at all.
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string>();

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "1 = 1", filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void BatchRead_ASelfJoinRelationship_AliasesTheSameTableUnderADifferentName()
    {
        // The primary table and the relationship's own table are identical ("Employee") — base and r0
        // must still be two distinct references, not a self-referencing ambiguity.
        var relationships = new List<RelationshipConfig> { Rel("manager", "Employee", ("ManagerId", "Id")) };
        var aliases = new Dictionary<string, string> { ["manager"] = "r0" };

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Employee] AS base
            LEFT JOIN [dbo].[Employee] AS r0 ON base.[ManagerId] = r0.[Id]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Employee", null, "1 = 1", filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void Range_AppliesTheFilterSoAutoBucketsCoverOnlyTheMappedSubset()
    {
        Assert.Equal(
            "SELECT MIN([OrderId]), MAX([OrderId]) FROM [dbo].[Orders] WHERE Region = 'EU'",
            BatchReloadStatement.BuildRange(BracketDialect.Instance, "dbo", "Orders", null, "OrderId", "Region = 'EU'"));
    }

    // ---- Phase 191S: query-shaped sources -------------------------------------------------------

    [Fact]
    public void BatchRead_AQueryWithNothingNeedingIt_RunsCompletelyUnwrapped()
    {
        // Absent a real segment, a relationship, or a forced wrap, a query-shaped source runs exactly
        // as the operator wrote it — no projection, no predicate, nothing substituted.
        Assert.Equal(
            "SELECT * FROM Region",
            BatchReloadStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Region", "1 = 1", filter: null));
    }

    [Fact]
    public void BatchRead_AQuery_WrapsWhenTheCallerSaysSomethingNeedsIt()
    {
        Assert.Equal(
            """
            SELECT * FROM (SELECT * FROM Region) AS base
            WHERE [Id] >= @segMin AND [Id] < @segMax
            """,
            BatchReloadStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Region",
                "[Id] >= @segMin AND [Id] < @segMax", filter: null, wrapQuery: true));
    }

    [Fact]
    public void BatchRead_AQueryWithARelationship_WrapsEvenWithWrapQueryFalse()
    {
        // The join itself is what forces the wrap here — wrapQuery only covers the two reasons
        // BuildRead can't see for itself (a real segment, a generated transform).
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            """
            SELECT * FROM (SELECT * FROM Orders) AS base
            LEFT JOIN [dbo].[Region] AS r0 ON base.[RegionId] = r0.[Id]
            WHERE 1 = 1
            """,
            BatchReloadStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Orders", "1 = 1", filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void Range_ATransformedColumn_SamplesTheTransformedExpression()
    {
        // Bucket boundaries computed against the raw column would land in the wrong value-space once
        // SourceProjection starts projecting the transformed one.
        Assert.Equal(
            "SELECT MIN([OrderId] * 2), MAX([OrderId] * 2) FROM [dbo].[Orders]",
            BatchReloadStatement.BuildRange(
                BracketDialect.Instance, "dbo", "Orders", null, "OrderId", filter: null, transform: "{{column}} * 2"));
    }

    [Fact]
    public void Range_AQuery_AlwaysWraps()
    {
        // Unlike BuildRead, there is no unwrapped alternative for an aggregate.
        Assert.Equal(
            "SELECT MIN(base.[OrderId]), MAX(base.[OrderId]) FROM (SELECT * FROM Orders) AS base",
            BatchReloadStatement.BuildRange(BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Orders", "OrderId", filter: null));
    }

    [Fact]
    public void Range_ARelationshipSourcedColumn_JoinsAndQualifiesTheAggregate()
    {
        // Phase 195S: auto-segmenting by a relationship's own column needs a real JOIN to sample
        // MIN/MAX through — BuildRange never rendered one before this.
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            "SELECT MIN(r0.[Name]), MAX(r0.[Name]) FROM [dbo].[Orders] AS base\nLEFT JOIN [dbo].[Region] AS r0 ON base.[RegionId] = r0.[Id]",
            BatchReloadStatement.BuildRange(
                BracketDialect.Instance, "dbo", "Orders", null, "Name", filter: null,
                relationships: relationships, relationshipAliases: aliases,
                reference: c => $"r0.{BracketDialect.Instance.QuoteIdentifier(c)}"));
    }

    [Fact]
    public void Delete_ScopesToTheSegmentRatherThanTheWholeTable()
    {
        Assert.Equal(
            "DELETE FROM [dbo].[Orders] WHERE [OrderId] >= @segMin AND [OrderId] < @segMax",
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
            WHERE [__Operation] <> 'D'
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
              AND [__Ordinal] <= @upToOrdinal
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
