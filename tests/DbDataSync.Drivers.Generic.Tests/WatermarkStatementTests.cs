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
            "SELECT MAX([ModifiedAt]) FROM [dbo].[Orders];",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", filter: null));
    }

    [Fact]
    public void MaxWatermark_AppendsAConfiguredFilterAsItsOwnWhereClause()
    {
        Assert.Equal(
            "SELECT MAX([ModifiedAt]) FROM [dbo].[Orders] WHERE Region = 'EU';",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", "Region = 'EU'"));
    }

    [Fact]
    public void Read_WithNoPreviousWatermark_MatchesTheOriginalMsSqlRenderingExactly()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE 1 = 1
            ORDER BY [ModifiedAt];
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: false, filter: null));
    }

    [Fact]
    public void Read_WithAPreviousWatermark_MatchesTheOriginalMsSqlRenderingExactly()
    {
        Assert.Equal(
            """
            SELECT * FROM [dbo].[Orders]
            WHERE [ModifiedAt] > @previousWatermark
            ORDER BY [ModifiedAt];
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: true, filter: null));
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
            ORDER BY [ModifiedAt];
            """,
            WatermarkStatement.BuildRead(BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: false, "Region = 'EU'"));
    }

    [Fact]
    public void Read_FollowsTheDialectForQuotingAndPlaceholders()
    {
        Assert.Equal(
            """
            SELECT * FROM "APP"."ORDERS"
            WHERE "MODIFIED_AT" > :previousWatermark
            ORDER BY "MODIFIED_AT";
            """,
            WatermarkStatement.BuildRead(ColonDialect.Instance, "APP", "ORDERS", "MODIFIED_AT", hasPreviousWatermark: true, filter: null));
    }

    [Fact]
    public void Read_Bounded_UsesTopWithTiesAndCarriesTheWatermarkBack()
    {
        Assert.Equal(
            """
            SELECT TOP (@maxRows) WITH TIES *, [ModifiedAt] AS [__DS_Position] FROM [dbo].[Orders]
            WHERE [ModifiedAt] > @previousWatermark
            ORDER BY [ModifiedAt];
            """,
            WatermarkStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: true,
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
            FETCH FIRST :maxRows ROWS WITH TIES;
            """,
            WatermarkStatement.BuildRead(
                ColonDialect.Instance, "APP", "ORDERS", "MODIFIED_AT", hasPreviousWatermark: true,
                filter: null, bounded: true));
    }

    [Fact]
    public void Read_Bounded_KeepsTheWatermarkColumnLast_SoExistingOrdinalsAreUndisturbed()
    {
        // The reader builds the change schema from the leading columns and reads the position off the
        // trailing one. If the position column ever moved, every row would gain a phantom column.
        var sql = WatermarkStatement.BuildRead(
            BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: false,
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
            ORDER BY [ModifiedAt];
            """,
            WatermarkStatement.BuildRead(
                BracketDialect.Instance, "dbo", "Orders", "ModifiedAt", hasPreviousWatermark: true, filter: null));
    }

    [Fact]
    public void QualifyTable_OmitsTheSchemaWhenThereIsNone() =>
        Assert.Equal(
            "SELECT MAX([ModifiedAt]) FROM [Orders];",
            WatermarkStatement.BuildMaxWatermark(BracketDialect.Instance, "", "Orders", "ModifiedAt", filter: null));
}
