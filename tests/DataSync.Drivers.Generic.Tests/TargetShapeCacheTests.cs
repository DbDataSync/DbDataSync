using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.Generic.Tests;

/// <summary>
/// <see cref="TargetShape.FromCachedColumns"/> is the phase 91 path every one of the six writers'
/// <c>ApplyAsync</c> now takes — <see cref="DeleteInsertWriter"/>, <see cref="SnapshotWriter"/> and
/// <see cref="Scd2Writer"/> here, <c>MsSqlDeleteInsertWriter</c>/<c>MsSqlMergeWriter</c>/
/// <c>MsSqlMergeReconcileWriter</c> through <c>MsSqlTargetShape.FromCachedColumns</c>, which mirrors this
/// exactly. Tested once, at the shared method, rather than six times against six writers: no
/// <see cref="ITableCatalog"/> parameter exists on this overload at all, so "zero live catalog calls" is
/// not a fake proving itself unused — it is a fact about the method's signature.
/// </summary>
public sealed class TargetShapeCacheTests
{
    private static readonly List<CachedColumn> TargetColumns =
    [
        new("Id", "int", false, true, false),
        new("Region", "nvarchar(20)", true, false, false),
        new("Amount", "decimal(18,2)", false, false, false),
    ];

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Amount", TargetColumn = "Amount" },
    ];

    private static TableRef Target() => new() { ConnectionName = "tgt", Database = "db", Schema = "dbo", Table = "Orders" };

    [Fact]
    public void APopulatedCache_ResolvesTheShapeWithNoCatalogInvolvedAtAll()
    {
        var shape = TargetShape.FromCachedColumns(BracketDialect.Instance, "orders", TargetColumns, Target(), Mappings);

        Assert.Equal(["Id", "Amount"], shape.MappedTargetColumns);
        Assert.Equal(["Id"], shape.PrimaryKeyColumns);
        Assert.False(shape.RequiresGeneratedColumnOverride);
        // The *full* cached set, not just the mapped columns — SegmentScope.Build resolves a reload's
        // segment column against this, and "Region" is not one either writer maps.
        Assert.Equal(3, shape.Columns.Count);
        Assert.Contains(shape.Columns, c => c.Name == "Region");
    }

    [Fact]
    public void AMappedIdentityColumn_IsDetectedFromTheCache()
    {
        List<CachedColumn> withIdentity =
        [
            new("Id", "int", false, true, true),
            new("Amount", "decimal(18,2)", false, false, false),
        ];

        var shape = TargetShape.FromCachedColumns(BracketDialect.Instance, "orders", withIdentity, Target(), Mappings);

        Assert.True(shape.RequiresGeneratedColumnOverride);
    }

    [Fact]
    public void AnEmptyCache_ThrowsNamingTheMappingAndTarget_BeforeLookingAtAnyMappedColumn()
    {
        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            TargetShape.FromCachedColumns(BracketDialect.Instance, "orders", [], Target(), Mappings));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Null(ex.Column);
    }

    [Fact]
    public void ACacheMissingAMappedColumn_ThrowsNamingIt()
    {
        List<CachedColumn> incomplete = [new("Id", "int", false, true, false)];

        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            TargetShape.FromCachedColumns(BracketDialect.Instance, "orders", incomplete, Target(), Mappings));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Equal("Amount", ex.Column);
    }
}
