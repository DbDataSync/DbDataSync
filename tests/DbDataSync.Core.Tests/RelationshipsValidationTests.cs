using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 186J's three shape-level checks on a mapping's declared relationships. Deliberately not
/// checking whether <see cref="RelationshipJoinKey.LocalColumn"/>/<see cref="RelationshipJoinKey.ForeignColumn"/>
/// resolve to real columns — that's a save-time metadata-refresh concern, the same way
/// <see cref="ColumnMapping.SourceColumn"/>/<see cref="ColumnMapping.TargetColumn"/> are never checked
/// against a live catalog here either. See <see cref="ConfigValidation.ValidateRelationships"/>.
/// </summary>
public sealed class RelationshipsValidationTests
{
    private static TableMappingConfig Mapping(
        List<RelationshipConfig>? relationships = null, List<ColumnMapping>? columnMappings = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
        Relationships = relationships ?? [],
        ColumnMappings = columnMappings ?? [],
    };

    private static RelationshipConfig Relationship(
        string name = "Customer", params RelationshipJoinKey[] joinKeys) => new()
    {
        Name = name, Table = "Customers", JoinKeys = [.. joinKeys],
    };

    private static RelationshipJoinKey JoinKey(string local = "CustomerId", string foreign = "Id") =>
        new() { LocalColumn = local, ForeignColumn = foreign };

    [Fact]
    public void ARelationshipWithAtLeastOneJoinKey_IsFine() =>
        ConfigValidation.ValidateRelationships(Mapping([Relationship(joinKeys: JoinKey())]));

    [Fact]
    public void ARelationshipWithNoJoinKeys_IsRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(
            () => ConfigValidation.ValidateRelationships(Mapping([Relationship()])));

        Assert.Contains("no join keys", problem.Message);
        Assert.Contains("Customer", problem.Message);
    }

    [Fact]
    public void TwoRelationshipsWithTheSameName_AreRejected()
    {
        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateRelationships(Mapping(
        [
            Relationship("Customer", JoinKey()),
            Relationship("Customer", JoinKey("BillingCustomerId")),
        ])));

        Assert.Contains("more than one relationship named 'Customer'", problem.Message);
    }

    [Fact]
    public void RelationshipNamesDifferingOnlyByCase_AreStillRejectedAsDuplicates() =>
        Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateRelationships(Mapping(
        [
            Relationship("Customer", JoinKey()),
            Relationship("customer", JoinKey()),
        ])));

    [Fact]
    public void AColumnMappingReferencingAnUndeclaredRelationship_IsRejected()
    {
        var mapping = Mapping(
            columnMappings: [new ColumnMapping { SourceColumn = "Region", TargetColumn = "Region", Relationship = "Customer" }]);

        var problem = Assert.Throws<ConfigValidationException>(() => ConfigValidation.ValidateRelationships(mapping));

        Assert.Contains("'Customer'", problem.Message);
        Assert.Contains("not declared", problem.Message);
    }

    [Fact]
    public void AColumnMappingReferencingADeclaredRelationship_IsFine()
    {
        var mapping = Mapping(
            [Relationship(joinKeys: JoinKey())],
            [new ColumnMapping { SourceColumn = "Region", TargetColumn = "Region", Relationship = "Customer" }]);

        ConfigValidation.ValidateRelationships(mapping);
    }

    [Fact]
    public void AColumnMappingWithNoRelationship_NeedsNoDeclarationAtAll() =>
        ConfigValidation.ValidateRelationships(Mapping(
            columnMappings: [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }]));

    [Fact]
    public void ASelfJoinRelationship_IsFine() =>
        // 185J/186J's confirmed decision: a relationship pointing back at the mapping's own primary
        // source table is ordinary, not rejected.
        ConfigValidation.ValidateRelationships(Mapping(
        [
            new RelationshipConfig { Name = "Manager", Table = "Orders", JoinKeys = [JoinKey("ManagerId", "Id")] },
        ]));
}
