namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// The staging provider's generated SQL and — the part with a known defect behind it — its batching
/// arithmetic.
/// </summary>
public sealed class StagingStatementTests
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "int",
        ["Name"] = "nvarchar(50)",
        ["Amount"] = "decimal(18,2)",
    };

    [Fact]
    public void Create_StagesEveryMappedColumnAsNullablePlusAnOperationMarker()
    {
        Assert.Equal(
            "CREATE TABLE [dbo].[DS_STG_x] ([Id] int NULL, [Name] nvarchar(50) NULL, [__Operation] CHAR(1) NOT NULL);",
            StagingStatement.BuildCreate(BracketDialect.Instance, "[dbo].[DS_STG_x]", ["Id", "Name"], Types));
    }

    [Fact]
    public void Create_KeepsTheTargetsFullTypeSpec_NotJustTheTypeName()
    {
        // A bare `nvarchar` defaults to length 1 and a bare `decimal` to scale 0, which would silently
        // truncate every staged value rather than fail.
        var sql = StagingStatement.BuildCreate(BracketDialect.Instance, "t", ["Name", "Amount"], Types);

        Assert.Contains("nvarchar(50)", sql);
        Assert.Contains("decimal(18,2)", sql);
    }

    [Fact]
    public void Insert_BindsEveryValueIncludingTheOperationMarker()
    {
        Assert.Equal(
            "INSERT INTO t ([Id], [Name], [__Operation]) VALUES " +
            "(@__s0_0, @__s0_1, @__s0_2), (@__s1_0, @__s1_1, @__s1_2);",
            StagingStatement.BuildInsert(BracketDialect.Instance, "t", ["Id", "Name"], rowCount: 2));
    }

    [Fact]
    public void Insert_FollowsTheDialectForQuotingAndPlaceholders()
    {
        Assert.Equal(
            "INSERT INTO t (\"Id\", \"__Operation\") VALUES (:__s0_0, :__s0_1);",
            StagingStatement.BuildInsert(ColonDialect.Instance, "t", ["Id"], rowCount: 1));
    }

    [Theory]
    // The measured defect this arithmetic exists for: a fixed 500-row batch of a 5-column table binds
    // 3000 parameters against SQL Server's 2100 cap. Derived from the column count, it is 419.
    [InlineData(5, 419)]
    [InlineData(2, 1049)]
    [InlineData(50, 41)]
    [InlineData(200, 10)]
    public void RowsPerStatement_IsDerivedFromTheColumnCount(int valuesPerRow, int expected) =>
        Assert.Equal(expected, StagingStatement.RowsPerStatement(BracketDialect.Instance, valuesPerRow));

    [Fact]
    public void RowsPerStatement_NeverDropsBelowOne()
    {
        // A table with more columns than the engine allows parameters cannot be staged one row at a
        // time either — but returning 0 would spin forever without ever flushing, which is a worse
        // failure than the statement the server rejects with an explanatory error.
        Assert.Equal(1, StagingStatement.RowsPerStatement(BracketDialect.Instance, valuesPerRow: 5000));
    }

    [Fact]
    public void RowsPerStatement_TracksTheDialectsOwnLimit()
    {
        Assert.True(
            StagingStatement.RowsPerStatement(GenerousDialect.Instance, 5) >
            StagingStatement.RowsPerStatement(BracketDialect.Instance, 5));
    }

    [Fact]
    public void EveryParameterInABatchIsDistinct()
    {
        // Row and value index both appear in the name. Omitting either would collide the moment a
        // batch held more than one row, binding every row to the last one's values.
        var names = Enumerable.Range(0, 3)
            .SelectMany(r => Enumerable.Range(0, 4).Select(v => StagingStatement.ParameterName(r, v)))
            .ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
