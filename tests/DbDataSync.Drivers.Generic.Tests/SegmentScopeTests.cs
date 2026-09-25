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

    // ---- Phase 192S: transform-consistent predicates ---------------------------------------------

    [Fact]
    public void TransformAwareReference_AppliesAMatchingColumnMappingsTransform()
    {
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "OrderId", TargetColumn = "OrderId", Transform = "{{column}} * 2" } };
        var reference = SegmentScope.TransformAwareReference("OrderId", mappings, BracketDialect.Instance.QuoteIdentifier);

        Assert.Equal("[OrderId] * 2", reference("OrderId"));
    }

    [Fact]
    public void TransformAwareReference_WithNoMatchingMapping_IsUnchanged()
    {
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "Region", TargetColumn = "Region", Transform = "UPPER({{column}})" } };
        var reference = SegmentScope.TransformAwareReference("OrderId", mappings, BracketDialect.Instance.QuoteIdentifier);

        Assert.Equal("[OrderId]", reference("OrderId"));
    }

    [Fact]
    public void TransformAwareReference_ForAPrimaryColumn_IgnoresARelationshipSourcedMappingSharingTheSameName()
    {
        // A primary-sourced segment (relationship left null, the default) must not accidentally pick up
        // a relationship-sourced mapping's own transform just because the column name matches.
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Id", TargetColumn = "Id", Relationship = "Customer", Transform = "{{column}} + 1" },
        };
        var reference = SegmentScope.TransformAwareReference("Id", mappings, BracketDialect.Instance.QuoteIdentifier);

        Assert.Equal("[Id]", reference("Id"));
    }

    // ---- Phase 195S: relationship-sourced segments ------------------------------------------------

    [Fact]
    public void TransformAwareReference_ForARelationshipColumn_AppliesThatRelationshipsOwnTransform()
    {
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Id", TargetColumn = "Id" }, // primary, same name — must not match instead
            new() { SourceColumn = "Id", TargetColumn = "CustomerId", Relationship = "Customer", Transform = "{{column}} + 1" },
        };
        var reference = SegmentScope.TransformAwareReference(
            "Id", mappings, BracketDialect.Instance.QuoteIdentifier, relationship: "Customer");

        Assert.Equal("[Id] + 1", reference("Id"));
    }

    [Fact]
    public void Build_WithARelationshipSourcedListSegment_ResolvesTheColumnFromThatRelationshipsCache()
    {
        var binder = new RecordingBinder();
        var relationshipColumns = new Dictionary<string, IReadOnlyList<ColumnMetadata>>
        {
            ["Customer"] = [new("Label", "nvarchar(20)", IsNullable: true, IsPrimaryKey: false, IsIdentity: false)],
        };
        var relationshipAliases = new Dictionary<string, string> { ["Customer"] = "r0" };
        var reference = SourceProjection.ReferenceFor(
            BracketDialect.Instance, "Customer", BracketDialect.Instance.QuoteIdentifier, relationshipAliases, "Segment column 'Label'");

        var scope = SegmentScope.Build(
            BracketDialect.Instance, binder, new ListSegment("Label", ["EU"], Relationship: "Customer"), Columns,
            reference: reference, relationshipColumns: relationshipColumns);

        Assert.Equal("r0.[Label] IN (@seg0)", scope.Predicate);
    }

    [Fact]
    public void Build_WithARelationshipSourcedSegment_AndNoRelationshipColumnsSupplied_ThrowsRatherThanMisresolve()
    {
        var binder = new RecordingBinder();

        var ex = Assert.Throws<InvalidOperationException>(() => SegmentScope.Build(
            BracketDialect.Instance, binder, new ListSegment("Label", ["EU"], Relationship: "Customer"), Columns));

        Assert.Contains("Customer", ex.Message);
    }

    [Fact]
    public void Build_ForAWriter_TranslatesARelationshipSourcedSegmentColumn_ThroughTheMatchingRelationship()
    {
        // A writer never sees a relationship's own cache (its target has no relationships at all) — the
        // segment's column translates to its target-side name via the mapping sharing *that* same
        // relationship, then resolves in the target's own flat column list, same as a primary column.
        var binder = new RecordingBinder();
        var mappings = new List<ColumnMapping>
        {
            new() { SourceColumn = "Label", TargetColumn = "Region" }, // primary, same source name — must not match instead
            new() { SourceColumn = "Label", TargetColumn = "Region", Relationship = "Customer" },
        };

        var scope = SegmentScope.Build(
            BracketDialect.Instance, binder, new ListSegment("Label", ["EU"], Relationship: "Customer"), Columns, mappings);

        Assert.Equal("[Region] IN (@seg0)", scope.Predicate);
    }

    [Fact]
    public void TransformConsistentPredicate_MatchesWhatSourceProjectionWouldEmit()
    {
        // The actual bug this phase fixes: a segment predicate built from the raw reference would
        // compare source-side bounds against a target column that stores the transformed value.
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "OrderId", TargetColumn = "OrderId", Transform = "{{column}} * 2" } };
        var reference = SegmentScope.TransformAwareReference("OrderId", mappings, BracketDialect.Instance.QuoteIdentifier);
        var binder = new RecordingBinder();

        var scope = SegmentScope.Build(BracketDialect.Instance, binder, new RangeSegment("OrderId", "1", "1000"), Columns, reference: reference);

        Assert.Equal("[OrderId] * 2 >= @segMin AND [OrderId] * 2 < @segMax", scope.Predicate);
    }
}
