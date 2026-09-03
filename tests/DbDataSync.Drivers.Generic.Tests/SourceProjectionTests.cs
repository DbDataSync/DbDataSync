using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// How a mapping's columns — and its source-dialect transforms — become a SELECT list. Pinned without
/// a server, because this is the layer where a transform stops being config and becomes SQL.
/// </summary>
public sealed class SourceProjectionTests
{
    private static ColumnMapping Map(string source, string? transform = null) =>
        new() { SourceColumn = source, TargetColumn = source, Transform = transform };

    [Fact]
    public void NoMappings_ProjectsEverything()
    {
        // Not the same as "project nothing": a reload triggered before any mapping exists, and every
        // driver test that reads directly, both land here and both want the whole row.
        Assert.Equal("*", SourceProjection.Render(BracketDialect.Instance, []));
    }

    [Fact]
    public void WithoutATransform_AColumnIsJustTheQuotedColumn()
    {
        Assert.Equal(
            "[Id], [Region]",
            SourceProjection.Render(BracketDialect.Instance, [Map("Id"), Map("Region")]));
    }

    [Fact]
    public void ATransform_SubstitutesTheColumnTokenAndAliasesBackToTheSourceName()
    {
        // Aliasing back to the *source* name is what keeps everything downstream unchanged: staging
        // still maps source names to target names, and ChangeSchema still carries source names.
        Assert.Equal(
            "[Id], UPPER([Region]) AS [Region]",
            SourceProjection.Render(BracketDialect.Instance, [Map("Id"), Map("Region", "UPPER({{column}})")]));
    }

    [Fact]
    public void ATransformWithoutTheToken_IsUsedVerbatim()
    {
        // A transform is not required to be *about* its own column — a literal, another column, or a
        // correlated subquery all have to stay expressible.
        Assert.Equal(
            "'FIXED' AS [Region]",
            SourceProjection.Render(BracketDialect.Instance, [Map("Region", "'FIXED'")]));
    }

    [Fact]
    public void TheTokenIsSubstitutedEverywhereItAppears()
    {
        Assert.Equal(
            "COALESCE([Region], UPPER([Region])) AS [Region]",
            SourceProjection.Render(BracketDialect.Instance, [Map("Region", "COALESCE({{column}}, UPPER({{column}}))")]));
    }

    [Fact]
    public void QuotingAndTheColumnReferenceFollowTheDialect()
    {
        Assert.Equal(
            "\"Id\", UPPER(\"Region\") AS \"Region\"",
            SourceProjection.Render(ColonDialect.Instance, [Map("Id"), Map("Region", "UPPER({{column}})")]));
    }

    [Fact]
    public void TheColumnReferenceCanBeOverridden_ForAStatementThatAliasesTheTable()
    {
        // The Change Tracking reader joins the source table as `base`, so an unqualified column name
        // there is ambiguous against CHANGETABLE's own copy of the key. This hook is why the token
        // exists at all.
        Assert.Equal(
            "base.[Id], UPPER(base.[Region]) AS [Region]",
            SourceProjection.Render(
                BracketDialect.Instance,
                [Map("Id"), Map("Region", "UPPER({{column}})")],
                c => $"base.[{c}]"));
    }

    [Fact]
    public void OneSourceColumnFeedingTwoTargets_IsSelectedOnce()
    {
        // Writing one source value into two target columns is legitimate; selecting it twice is not,
        // and would hand ChangeSchema a duplicate name.
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Region", TargetColumn = "Region" },
            new() { SourceColumn = "Region", TargetColumn = "RegionCopy" },
        };

        Assert.Equal("[Region]", SourceProjection.Render(BracketDialect.Instance, mappings));
    }
}
