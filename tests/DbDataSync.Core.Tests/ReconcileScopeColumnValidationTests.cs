using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 192S (190S's own deferred decision 7): a reconciling writer's delete-scope predicate
/// translates the segment/watermark column's name to its target-side one, so that column has to be
/// mapped — see <see cref="ConfigValidation.ValidateReconcileScopeColumn"/>.
/// </summary>
public sealed class ReconcileScopeColumnValidationTests
{
    private static TableMappingConfig Mapping(List<ColumnMapping>? columnMappings = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        ColumnMappings = columnMappings ?? [],
    };

    private static Dictionary<string, string> SegmentOptions(BatchReloadSegment segment) =>
        new() { [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment) };

    [Fact]
    public void ANonReconcilingWriter_IsNeverChecked() =>
        // "Snapshot" doesn't reconcile — an unmapped segment column is nobody's problem here.
        ConfigValidation.ValidateReconcileScopeColumn(
            Mapping(), "BatchReload", "Snapshot", SegmentOptions(new ListSegment("Region", ["EU"])));

    [Fact]
    public void AReconcilingWriter_WithNoSegmentConfigured_IsFine() =>
        ConfigValidation.ValidateReconcileScopeColumn(Mapping(), "BatchReload", "DeleteInsert", new Dictionary<string, string>());

    [Fact]
    public void AReconcilingWriter_WithAMappedSegmentColumn_IsFine() =>
        ConfigValidation.ValidateReconcileScopeColumn(
            Mapping([new() { SourceColumn = "Region", TargetColumn = "Region" }]),
            "BatchReload", "DeleteInsert", SegmentOptions(new ListSegment("Region", ["EU"])));

    [Fact]
    public void AReconcilingWriter_WithAnUnmappedSegmentColumn_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateReconcileScopeColumn(
            Mapping(), "BatchReload", "DeleteInsert", SegmentOptions(new RangeSegment("OrderId", "1", "1000"))));

        Assert.Contains("'OrderId'", problem.Message);
        Assert.Contains("DeleteInsert", problem.Message);
    }

    [Fact]
    public void AReconcilingWriter_WithAnUnmappedAutoSegmentColumn_IsRejected() =>
        Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateReconcileScopeColumn(
            Mapping(), "BatchReload", "KeyReconcileDelete", SegmentOptions(new AutoSegment("OrderId", 4))));

    [Fact]
    public void AWatermarkReader_WithAnUnmappedWatermarkColumn_IsRejected()
    {
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" };

        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateReconcileScopeColumn(Mapping(), "Watermark", "MsSqlMergeReconcile", options));

        Assert.Contains("'ModifiedAt'", problem.Message);
    }

    [Fact]
    public void AWatermarkReader_WithAMappedWatermarkColumn_IsFine()
    {
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" };
        var mapping = Mapping([new() { SourceColumn = "ModifiedAt", TargetColumn = "ModifiedAt" }]);

        ConfigValidation.ValidateReconcileScopeColumn(mapping, "Watermark", "MsSqlDeleteInsert", options);
    }

    [Fact]
    public void ARelationshipSourcedMapping_SharingTheSegmentColumnsName_DoesNotCount()
    {
        // The scoping column always names the *primary* source — a relationship-sourced ColumnMapping
        // that happens to share its bare name is not the same column and must not satisfy this check.
        var mapping = Mapping([new() { SourceColumn = "Region", TargetColumn = "Region", Relationship = "Customer" }]);

        Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateReconcileScopeColumn(
            mapping, "BatchReload", "DeleteInsert", SegmentOptions(new ListSegment("Region", ["EU"]))));
    }

    // ---- Phase 195S: relationship-sourced scoping columns -----------------------------------------

    [Fact]
    public void AReconcilingWriter_WithARelationshipSourcedSegmentColumn_MappedOnThatSameRelationship_IsFine()
    {
        var mapping = Mapping([new() { SourceColumn = "Label", TargetColumn = "RegionLabel", Relationship = "Customer" }]);

        ConfigValidation.ValidateReconcileScopeColumn(
            mapping, "BatchReload", "DeleteInsert", SegmentOptions(new ListSegment("Label", ["EU"], Relationship: "Customer")));
    }

    [Fact]
    public void AReconcilingWriter_WithARelationshipSourcedSegmentColumn_MappedOnADifferentRelationship_IsRejected()
    {
        // Two relationships can each have a same-named column — a mapping for the wrong one must not
        // silently satisfy this check.
        var mapping = Mapping([new() { SourceColumn = "Label", TargetColumn = "RegionLabel", Relationship = "Vendor" }]);

        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateReconcileScopeColumn(
            mapping, "BatchReload", "DeleteInsert", SegmentOptions(new ListSegment("Label", ["EU"], Relationship: "Customer"))));

        Assert.Contains("'Label'", problem.Message);
        Assert.Contains("'Customer'", problem.Message);
    }

    [Fact]
    public void AReconcilingWriter_WithAnUnmappedRelationshipSourcedSegmentColumn_IsRejected() =>
        Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateReconcileScopeColumn(
            Mapping(), "BatchReload", "DeleteInsert", SegmentOptions(new ListSegment("Label", ["EU"], Relationship: "Customer"))));

    [Fact]
    public void AWatermarkReader_WithARelationshipSourcedWatermarkColumn_MappedOnThatSameRelationship_IsFine()
    {
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt", ["watermarkRelationship"] = "Customer" };
        var mapping = Mapping([new() { SourceColumn = "ModifiedAt", TargetColumn = "CustomerModifiedAt", Relationship = "Customer" }]);

        ConfigValidation.ValidateReconcileScopeColumn(mapping, "Watermark", "MsSqlDeleteInsert", options);
    }

    [Fact]
    public void AWatermarkReader_WithAnUnmappedRelationshipSourcedWatermarkColumn_IsRejected()
    {
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt", ["watermarkRelationship"] = "Customer" };

        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateReconcileScopeColumn(Mapping(), "Watermark", "MsSqlMergeReconcile", options));

        Assert.Contains("'ModifiedAt'", problem.Message);
        Assert.Contains("'Customer'", problem.Message);
    }

    [Fact]
    public void AWatermarkReader_WithABlankWatermarkRelationship_IsTreatedAsPrimarySourced()
    {
        // An empty string (the option's own "not set" spelling) must not be treated as a real
        // relationship name that then fails to match anything.
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt", ["watermarkRelationship"] = "" };
        var mapping = Mapping([new() { SourceColumn = "ModifiedAt", TargetColumn = "ModifiedAt" }]);

        ConfigValidation.ValidateReconcileScopeColumn(mapping, "Watermark", "MsSqlDeleteInsert", options);
    }
}
