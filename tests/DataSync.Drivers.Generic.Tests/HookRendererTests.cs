namespace DataSync.Drivers.Generic.Tests;

/// <summary>Token substitution (identifiers, quoted, textual) and parameter substitution (values,
/// bound, only the ones actually referenced) — see phase 26's "identifiers are substituted; values are
/// bound".</summary>
public sealed class HookRendererTests
{
    private static readonly HookRenderContext Context = new(
        TargetQualified: "[dbo].[Orders]",
        TargetSchemaQuoted: "[dbo]",
        TargetTableQuoted: "[Orders]",
        SourceQualified: "[dbo].[SrcOrders]",
        StagingQualified: "[dbo].[DS_STG_x]",
        Replication: "sync",
        Mapping: "orders",
        RunId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RunKind: "Primary",
        Segment: null,
        SegmentIndex: 0,
        SegmentCount: 1,
        IsLastSegment: true,
        RowsStaged: 42,
        RowsWritten: 40,
        Watermark: "12345");

    [Fact]
    public void SubstitutesEveryBuiltInToken()
    {
        var statement = HookRenderer.Render(
            BracketDialect.Instance,
            "-- {{target}} {{targetSchema}} {{targetTable}} {{source}} {{staging}}",
            new Dictionary<string, string>(),
            Context);

        Assert.Equal("-- [dbo].[Orders] [dbo] [Orders] [dbo].[SrcOrders] [dbo].[DS_STG_x]", statement.CommandText);
    }

    [Fact]
    public void OnlyReferencedParametersAreBound()
    {
        var statement = HookRenderer.Render(BracketDialect.Instance, "SELECT @mapping;", new Dictionary<string, string>(), Context);

        var parameter = Assert.Single(statement.Parameters);
        Assert.Equal("mapping", parameter.Name);
        Assert.Equal("orders", parameter.Value);
    }

    [Fact]
    public void AnUnreferencedParameter_IsNeverAdded()
    {
        var statement = HookRenderer.Render(BracketDialect.Instance, "SELECT @mapping;", new Dictionary<string, string>(), Context);

        Assert.DoesNotContain(statement.Parameters, p => p.Name == "rowsWritten");
    }

    [Fact]
    public void ANullFactBinds_DBNull_NotBareNull()
    {
        var context = Context with { Watermark = null };
        var statement = HookRenderer.Render(BracketDialect.Instance, "SELECT @watermark;", new Dictionary<string, string>(), context);

        Assert.Equal(DBNull.Value, Assert.Single(statement.Parameters).Value);
    }

    [Fact]
    public void FollowsTheDialectForParameterPlaceholders()
    {
        var statement = HookRenderer.Render(ColonDialect.Instance, "SELECT @mapping;", new Dictionary<string, string>(), Context);

        Assert.Equal("SELECT :mapping;", statement.CommandText);
    }

    [Fact]
    public void ADeclaredParameter_IsSubstitutedAsAQuotedIdentifier()
    {
        var statement = HookRenderer.Render(
            BracketDialect.Instance, "ALTER INDEX ALL ON {{controlTable}} REBUILD;",
            new Dictionary<string, string> { ["controlTable"] = "LoadControl" }, Context);

        Assert.Equal("ALTER INDEX ALL ON [LoadControl] REBUILD;", statement.CommandText);
    }

    [Fact]
    public void ADeclaredParameterValueThatIsQualified_IsQuotedAsSchemaDotTable()
    {
        var statement = HookRenderer.Render(
            BracketDialect.Instance, "INSERT INTO {{controlTable}} DEFAULT VALUES;",
            new Dictionary<string, string> { ["controlTable"] = "dbo.LoadControl" }, Context);

        Assert.Equal("INSERT INTO [dbo].[LoadControl] DEFAULT VALUES;", statement.CommandText);
    }

    [Fact]
    public void AnUnreferencedToken_IsLeftAlone_NoOpNotAnError()
    {
        var statement = HookRenderer.Render(BracketDialect.Instance, "SELECT 1;", new Dictionary<string, string>(), Context);

        Assert.Equal("SELECT 1;", statement.CommandText);
    }
}
