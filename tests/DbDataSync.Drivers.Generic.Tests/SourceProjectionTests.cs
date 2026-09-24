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
            "[Id],\n    [Region]",
            SourceProjection.Render(BracketDialect.Instance, [Map("Id"), Map("Region")]));
    }

    [Fact]
    public void ATransform_SubstitutesTheColumnTokenAndAliasesBackToTheSourceName()
    {
        // Aliasing back to the *source* name is what keeps everything downstream unchanged: staging
        // still maps source names to target names, and ChangeSchema still carries source names.
        Assert.Equal(
            "[Id],\n    UPPER([Region]) AS [Region]",
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
            "\"Id\",\n    UPPER(\"Region\") AS \"Region\"",
            SourceProjection.Render(ColonDialect.Instance, [Map("Id"), Map("Region", "UPPER({{column}})")]));
    }

    [Fact]
    public void TheColumnReferenceCanBeOverridden_ForAStatementThatAliasesTheTable()
    {
        // The Change Tracking reader joins the source table as `base`, so an unqualified column name
        // there is ambiguous against CHANGETABLE's own copy of the key. This hook is why the token
        // exists at all.
        Assert.Equal(
            "base.[Id],\n    UPPER(base.[Region]) AS [Region]",
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

    private static ColumnMapping RelMap(string relationship, string source, string? transform = null) =>
        new() { SourceColumn = source, TargetColumn = source, Relationship = relationship, Transform = transform };

    [Fact]
    public void ARelationshipSourcedColumn_IsReadOffItsOwnJoinAlias()
    {
        var mappings = new List<ColumnMapping> { Map("Id"), RelMap("region", "Name") };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            "[Id],\n    r0.[Name]",
            SourceProjection.Render(BracketDialect.Instance, mappings, relationshipAliases: aliases));
    }

    [Fact]
    public void ARelationshipSourcedColumn_WithATransform_SubstitutesTheJoinAliasReference()
    {
        var mappings = new List<ColumnMapping> { RelMap("region", "Name", "UPPER({{column}})") };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            "UPPER(r0.[Name]) AS [Name]",
            SourceProjection.Render(BracketDialect.Instance, mappings, relationshipAliases: aliases));
    }

    [Fact]
    public void TwoRelationships_EachUseTheirOwnAssignedAlias()
    {
        var mappings = new List<ColumnMapping> { RelMap("region", "Name"), RelMap("manager", "Name") };
        var aliases = new Dictionary<string, string> { ["region"] = "r0", ["manager"] = "r1" };

        Assert.Equal(
            "r0.[Name],\n    r1.[Name]",
            SourceProjection.Render(BracketDialect.Instance, mappings, relationshipAliases: aliases));
    }

    [Fact]
    public void APrimaryColumnAndARelationshipColumn_SharingATextualName_AreBothSelectedNotDeduped()
    {
        // A foreign lookup table sharing a column name with the primary table (both have "Name") is a
        // real, common case, not a contrived one — keying the SELECT-list dedupe on SourceColumn alone
        // would treat these as "the same source column" and silently drop the relationship's, feeding
        // both target columns the primary table's value instead.
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Name", TargetColumn = "Name" },
            new() { SourceColumn = "Name", TargetColumn = "RegionName", Relationship = "region" },
        };
        var aliases = new Dictionary<string, string> { ["region"] = "r0" };

        Assert.Equal(
            "[Name],\n    r0.[Name]",
            SourceProjection.Render(BracketDialect.Instance, mappings, relationshipAliases: aliases));
    }

    [Fact]
    public void ARelationshipNotInTheAliasMap_ThrowsRatherThanRenderingWrongSql()
    {
        var mappings = new List<ColumnMapping> { RelMap("region", "Name") };

        Assert.Throws<InvalidOperationException>(
            () => SourceProjection.Render(BracketDialect.Instance, mappings));
    }
}
