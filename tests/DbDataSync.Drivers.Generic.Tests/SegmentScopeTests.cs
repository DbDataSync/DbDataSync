using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// Predicate rendering, per dialect, with no server. The MSSQL suite pins what this produces for SQL
/// Server; these tests pin that the *shape* is the dialect's only variable — which is the whole claim
/// the generic layer makes.
/// </summary>
public sealed class SegmentScopeTests
{
    private static readonly List<ColumnMetadata> Columns =
    [
        new("OrderId", "int", IsNullable: false, IsPrimaryKey: true, IsIdentity: false),
        new("Region", "nvarchar(20)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false),
    ];

    private static SegmentScope Build(SqlDialect dialect, BatchReloadSegment? segment, out RecordingBinder binder)
    {
        binder = new RecordingBinder();
        return SegmentScope.Build(dialect, binder, segment, Columns);
    }

    [Fact]
    public void Full_MatchesEverythingAndBindsNothing()
    {
        var scope = Build(BracketDialect.Instance, new FullSegment(), out _);

        Assert.Equal("1 = 1", scope.Predicate);
        Assert.Empty(scope.Parameters);
    }

    [Fact]
    public void NoSegment_RendersTheSameAsFull() =>
        Assert.Equal("1 = 1", Build(ColonDialect.Instance, null, out _).Predicate);

    [Fact]
    public void List_QuotesAndPlaceholdersFollowTheDialect()
    {
        var brackets = Build(BracketDialect.Instance, new ListSegment("Region", ["EU", "US"]), out _);
        var colons = Build(ColonDialect.Instance, new ListSegment("Region", ["EU", "US"]), out _);

        Assert.Equal("[Region] IN (@seg0, @seg1)", brackets.Predicate);
        Assert.Equal("\"Region\" IN (:seg0, :seg1)", colons.Predicate);
    }

    [Fact]
    public void Range_IsHalfOpenInEveryDialect()
    {
        var brackets = Build(BracketDialect.Instance, new RangeSegment("OrderId", "1", "1000"), out _);
        var colons = Build(ColonDialect.Instance, new RangeSegment("OrderId", "1", "1000"), out _);

        Assert.Equal("[OrderId] >= @segMin AND [OrderId] < @segMax", brackets.Predicate);
        Assert.Equal("\"OrderId\" >= :segMin AND \"OrderId\" < :segMax", colons.Predicate);
    }

    [Fact]
    public void BoundParameterNames_ComeFromParameterName_NotFromTheStatementPlaceholder()
    {
        // The distinction the two dialect methods exist for: Oracle's statement says `:segMin` while
        // the parameter it binds is named `segMin`. Rendering placeholders from the parameter's own
        // name — the obvious shortcut — produces a statement that cannot bind on such a provider.
        Build(ColonDialect.Instance, new RangeSegment("OrderId", "1", "1000"), out var binder);

        Assert.Equal(["segMin", "segMax"], binder.Calls.Select(c => c.Name));
        Assert.Equal(["1", "1000"], binder.Calls.Select(c => c.Value));
    }

    [Fact]
    public void Column_IsTranslatedThroughTheMappingsWhenScopingATarget()
    {
        var binder = new RecordingBinder();
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "SourceRegion", TargetColumn = "Region" } };

        var scope = SegmentScope.Build(
            BracketDialect.Instance, binder, new ListSegment("SourceRegion", ["EU"]), Columns, mappings);

        Assert.Equal("[Region] IN (@seg0)", scope.Predicate);
    }

    [Fact]
    public void UnknownColumn_NamesWhatWasAvailable()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(BracketDialect.Instance, new ListSegment("Nope", ["x"]), out _));

        Assert.Contains("OrderId, Region", ex.Message);
    }

    [Fact]
    public void EmptyList_IsRejectedRatherThanRenderedAsMatchingNothing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(BracketDialect.Instance, new ListSegment("Region", []), out _));

        Assert.Contains("delete the target's entire scope", ex.Message);
    }

    [Fact]
    public void AutoSegment_IsRejectedBecauseItShouldHaveBeenExpandedAtEnqueue()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build(BracketDialect.Instance, new AutoSegment("OrderId", 4), out _));

        Assert.Contains("reached execution unexpanded", ex.Message);
    }
}
