using DataSync.Core.Config;
using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// Both sides of a check are generated from one declaration, and the whole point is that the operator
/// names a column once. These assert what each side ends up asking for.
/// </summary>
public sealed class VerificationStatementTests
{
    /// <summary>Double quotes, so the assertions below read as the SQL an ANSI engine would get.</summary>
    private static readonly SqlDialect Dialect = new ColonDialect();

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "cust_id", TargetColumn = "CustomerId" },
        new() { SourceColumn = "region", TargetColumn = "Region" },
        new() { SourceColumn = "amt", TargetColumn = "Amount" },
        new() { SourceColumn = "name", TargetColumn = "Name", Transform = "UPPER({{column}})" },
    ];

    [Fact]
    public void AnUngroupedRowCount_CountsTheWholeTable()
    {
        var sql = VerificationStatement.BuildRowCount(Dialect, "dbo", "Orders", [], filter: null);

        Assert.Equal("""SELECT COUNT(*) AS "__rows" FROM "dbo"."Orders";""", sql);
    }

    /// <summary>
    /// Ordered by the grouping columns, and not for tidiness: the two result sets are compared by a
    /// merge join, so both arriving in the same order is what lets it hold one row from each side
    /// rather than the whole of both.
    /// </summary>
    [Fact]
    public void AGroupedCount_GroupsAndOrdersByTheSameExpressions()
    {
        var columns = VerificationStatement.ResolveTarget(Dialect, ["Region"]);

        var sql = VerificationStatement.BuildRowCount(Dialect, "dbo", "Orders", columns, filter: null);

        Assert.Equal(
            """SELECT "Region" AS "Region", COUNT(*) AS "__rows" FROM "dbo"."Orders" GROUP BY "Region" ORDER BY "Region";""",
            sql);
    }

    /// <summary>
    /// An alias is not in scope in GROUP BY on SQL Server, and the engines that allow it are the
    /// exception — so the expression is repeated rather than referenced.
    /// </summary>
    [Fact]
    public void GroupBy_RepeatsTheExpressionRatherThanReferencingTheAlias()
    {
        var columns = VerificationStatement.ResolveSource(Dialect, Mappings, ["Name"]);

        var sql = VerificationStatement.BuildRowCount(Dialect, "dbo", "Orders", columns, filter: null);

        Assert.Contains("""GROUP BY UPPER("name")""", sql);
        Assert.DoesNotContain("""GROUP BY "Name" """.TrimEnd(), sql);
    }

    /// <summary>
    /// The column an operator names is the **target's**, and the source's statement is derived — which
    /// is what makes an aliased column one selection rather than two things to keep in step.
    /// </summary>
    [Fact]
    public void TheSourceSideTranslatesTargetNamesThroughTheMapping()
    {
        var source = VerificationStatement.ResolveSource(Dialect, Mappings, ["CustomerId", "Region"]);
        var target = VerificationStatement.ResolveTarget(Dialect, ["CustomerId", "Region"]);

        Assert.Equal(["""  "cust_id" """.Trim(), """ "region" """.Trim()], source.Select(c => c.Expression));
        Assert.Equal(["""  "CustomerId" """.Trim(), """ "Region" """.Trim()], target.Select(c => c.Expression));

        // Both sides label the result with the target's name, so the two sets have the same shape.
        Assert.Equal(target.Select(c => c.ResultName), source.Select(c => c.ResultName));
    }

    /// <summary>
    /// A transform runs on the way to the target, so the target holds the transformed value. Grouping
    /// the source by the raw column would compare 'alice' against 'ALICE' and report a difference that
    /// is not one.
    /// </summary>
    [Fact]
    public void ATransformedColumn_IsGroupedByWhatTheTransformProduces()
    {
        var source = VerificationStatement.ResolveSource(Dialect, Mappings, ["Name"]);

        Assert.Equal("""UPPER("name")""", Assert.Single(source).Expression);
    }

    [Fact]
    public void AColumnThisMappingDoesNotWrite_IsRefusedRatherThanCompared()
    {
        var ex = Assert.Throws<ConfigValidationException>(
            () => VerificationStatement.ResolveSource(Dialect, Mappings, ["NotMapped"]));

        Assert.Contains("NotMapped", ex.Message);
        Assert.Contains("nothing on the source to compare", ex.Message);
    }

    [Fact]
    public void ASum_AggregatesEachMeasureUnderItsTargetName()
    {
        var groupBy = VerificationStatement.ResolveSource(Dialect, Mappings, ["Region"]);
        var measures = VerificationStatement.ResolveSource(Dialect, Mappings, ["Amount"]);

        var sql = VerificationStatement.BuildSum(Dialect, "dbo", "Orders", groupBy, measures, filter: null);

        Assert.Equal(
            """SELECT "region" AS "Region", SUM("amt") AS "Amount" FROM "dbo"."Orders" GROUP BY "region" ORDER BY "region";""",
            sql);
    }

    [Fact]
    public void ASumWithNoMeasures_IsRefused() =>
        Assert.Throws<ConfigValidationException>(
            () => VerificationStatement.BuildSum(Dialect, "dbo", "Orders", [], [], filter: null));

    /// <summary>The same shape as a mapping's own source filter, so an operator narrowing a check to a
    /// quiet window uses a pattern they already know.</summary>
    [Fact]
    public void AFilter_NarrowsBothSidesTheSameWay()
    {
        var sql = VerificationStatement.BuildRowCount(
            Dialect, "dbo", "Orders", VerificationStatement.ResolveTarget(Dialect, ["Region"]),
            filter: "Status <> 'draft'");

        // Parenthesised since phase 54, which can AND a current-only predicate onto it: an operator's
        // filter is an arbitrary expression, and one containing an OR would otherwise swallow the AND
        // and silently compare every version again.
        Assert.Contains("WHERE (Status <> 'draft') GROUP BY", sql);
    }
}
