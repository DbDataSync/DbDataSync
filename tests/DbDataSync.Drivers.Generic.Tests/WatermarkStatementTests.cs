using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// The watermark reader moved out of the MSSQL driver onto a dialect, and the phase claimed that
/// needs no config migration because the SQL is unchanged. These tests hold the SQL Server rendering
/// to the exact text the deleted <c>MsSqlWatermarkReader</c> produced, so that claim stays checked
/// rather than remembered.
/// </summary>
public sealed class WatermarkStatementTests
{
    [Fact]
    public void MaxWatermark_MatchesTheOriginalMsSqlRenderingExactly()
    {
        Assert.Equal(
            "SELECT MAX([ModifiedAt]) FROM [dbo].[Orders]",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", filter: null));
    }

    [Fact]
    public void MaxWatermark_AppendsAConfiguredFilterAsItsOwnWhereClause()
    {
        Assert.Equal(
            "SELECT MAX([ModifiedAt]) FROM [dbo].[Orders] WHERE Region = 'EU'",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", "Region = 'EU'"));
    }

    [Fact]
    public void Read_WithNoPreviousWatermark_MatchesTheOriginalMsSqlRenderingExactly()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: false, filter: null));
    }

    [Fact]
    public void Read_WithAPreviousWatermark_MatchesTheOriginalMsSqlRenderingExactly()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE [ModifiedAt] > @previousWatermark
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: true, filter: null));
    }

    [Fact]
    public void Read_CombinesTheWatermarkPredicateAndTheConfiguredFilterWithAnd()
    {
        // The `1 = 1` on a first read is not noise: it is what lets the filter always be appended with
        // AND instead of the builder having to know whether it is emitting the first predicate.
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1 AND (Region = 'EU')
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: false, filter: "Region = 'EU'"));
    }

    [Fact]
    public void Read_FollowsTheDialectForQuotingAndPlaceholders()
    {
        Assert.Equal(
            """
            SELECT * FROM "APP"."ORDERS"
            WHERE "MODIFIED_AT" > :previousWatermark
            ORDER BY "MODIFIED_AT"
            """,
            WatermarkStatement.BuildRead(ColonDialect.Instance, "APP", "ORDERS", null, "MODIFIED_AT", hasPreviousWatermark: true, filter: null));
    }

    [Fact]
    public void Read_Bounded_UsesTopWithTiesAndCarriesTheWatermarkBack()
    {
        Assert.Equal(
            """
            SELECT TOP (@maxRows) WITH TIES *, [ModifiedAt] AS [__DS_Position] FROM [dbo].[Orders]
            WHERE [ModifiedAt] > @previousWatermark
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: true,
                filter: null, bounded: true));
    }

    [Fact]
    public void Read_Bounded_OnAnAnsiDialect_PutsTheLimitAtTheEndInstead()
    {
        // Same guarantee, opposite end of the statement. Both spellings include ties natively, which
        // is the property the whole bounded read rests on: a row sharing the boundary value is never
        // left behind for a next pass that will only look strictly past it.
        Assert.Equal(
            """
            SELECT *, "MODIFIED_AT" AS "__DS_Position" FROM "APP"."ORDERS"
            WHERE "MODIFIED_AT" > :previousWatermark
            ORDER BY "MODIFIED_AT"
            FETCH FIRST :maxRows ROWS WITH TIES
            """,
            WatermarkStatement.BuildRead(ColonDialect.Instance, "APP", "ORDERS", null, "MODIFIED_AT", hasPreviousWatermark: true,
                filter: null, bounded: true));
    }

    [Fact]
    public void Read_Bounded_KeepsTheWatermarkColumnLast_SoExistingOrdinalsAreUndisturbed()
    {
        // The reader builds the change schema from the leading columns and reads the position off the
        // trailing one. If the position column ever moved, every row would gain a phantom column.
        var sql = WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: false,
            filter: null, projection: "[Id], [Name]", bounded: true);

        Assert.Contains("[Id], [Name], [ModifiedAt] AS [__DS_Position] FROM", sql);
    }

    [Fact]
    public void Read_Unbounded_IsUnchangedByTheBoundedOptionExisting()
    {
        // The default path has to render byte-for-byte what it did before bounding was an option —
        // this is an incremental reader that runs constantly, and a stray clause would change its plan.
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE [ModifiedAt] > @previousWatermark
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: true, filter: null));
    }

    private static RelationshipConfig Rel(string name, string table, params (string Local, string Foreign)[] joinKeys) =>
        new()
        {
            Name = name,
            Table = table,
            JoinKeys = joinKeys.Select(k => new RelationshipJoinKey { LocalColumn = k.Local, ForeignColumn = k.Foreign }).ToList(),
        };

    [Fact]
    public void Read_WithARelationship_JoinsAndQualifiesTheWatermarkColumn()
    {
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            """
            SELECT [Id],
                r0.[Name] FROM [dbo].[Orders] AS base
            LEFT JOIN [dbo].[Region] AS r0 ON base.[RegionId] = r0.[Id]
            WHERE base.[ModifiedAt] > @previousWatermark
            ORDER BY base.[ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: true, filter: null,
                projection: "[Id],\n    r0.[Name]", relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void Read_WithMultipleRelationships_AliasesEachR0R1()
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
            ORDER BY base.[ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: false, filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void Read_ARelationshipDeclaredButNotReferenced_RendersNoJoinAtAll_AndLeavesTheColumnUnqualified()
    {
        var relationships = new List<RelationshipConfig> { Rel("region", "Region", ("RegionId", "Id")) };
        var aliases = new Dictionary<string, string>();

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1
            ORDER BY [ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: false, filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void Read_ASelfJoinRelationship_AliasesTheSameTableUnderADifferentName()
    {
        var relationships = new List<RelationshipConfig> { Rel("manager", "Employee", ("ManagerId", "Id")) };
        var aliases = new Dictionary<string, string> { ["manager"] = "r0" };

        Assert.Equal(
            """
            SELECT * FROM [dbo].[Employee] AS base
            LEFT JOIN [dbo].[Employee] AS r0 ON base.[ManagerId] = r0.[Id]
            WHERE 1 = 1
            ORDER BY base.[ModifiedAt]
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Employee", null, "ModifiedAt", hasPreviousWatermark: false, filter: null,
                relationships: relationships, relationshipAliases: aliases));
    }

    [Fact]
    public void QualifyTable_OmitsTheSchemaWhenThereIsNone() =>
        Assert.Equal(
            "SELECT MAX([ModifiedAt]) FROM [Orders]",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "", "Orders", null, "ModifiedAt", filter: null));

    // ---- Phase 191S: query-shaped sources -------------------------------------------------------

    [Fact]
    public void Read_AQuery_AlwaysWraps_EvenWithNoPreviousWatermarkAndNoRelationships()
    {
        // Watermark reading needs ORDER BY for its tie-safe bounded-read guarantee on essentially every
        // pass, so wrapping is unconditional here — unlike a reload, there is no "run it unwrapped"
        // alternative once the source is query-shaped.
        Assert.Equal(
            """
            SELECT * FROM (SELECT * FROM Orders) AS base
            WHERE 1 = 1
            ORDER BY base.[ModifiedAt]
            """,
            WatermarkStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Orders", "ModifiedAt",
                hasPreviousWatermark: false, filter: null));
    }

    [Fact]
    public void Read_ATransformedWatermarkColumn_FiltersAndOrdersByTheTransformedExpression()
    {
        // The predicate, the ORDER BY, and the bounded position column all have to agree with each
        // other and with what the target actually stores — they're all built from the same reference.
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE UPPER([ModifiedAt]) > @previousWatermark
            ORDER BY UPPER([ModifiedAt])
            """,
            WatermarkStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", hasPreviousWatermark: true,
                filter: null, transform: "UPPER({{column}})"));
    }

    [Fact]
    public void MaxWatermark_ATransformedColumn_TakesTheMaximumOfTheTransformedExpression() =>
        Assert.Equal(
            "SELECT MAX(UPPER([ModifiedAt])) FROM [dbo].[Orders]",
            WatermarkStatement.BuildMaxWatermark(
                BracketDialect.Instance, "dbo", "Orders", null, "ModifiedAt", filter: null, transform: "UPPER({{column}})"));

    [Fact]
    public void MaxWatermark_AQuery_AlwaysWraps() =>
        Assert.Equal(
            "SELECT MAX(base.[ModifiedAt]) FROM (SELECT * FROM Orders) AS base",
            WatermarkStatement.BuildMaxWatermark(
                BracketDialect.Instance, "dbo", "Orders", "SELECT * FROM Orders", "ModifiedAt", filter: null));
}
