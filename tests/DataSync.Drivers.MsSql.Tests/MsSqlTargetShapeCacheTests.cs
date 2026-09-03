using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql.Tests;

/// <summary>
/// <see cref="MsSqlTargetShape.FromCachedColumns"/> mirrors
/// <see cref="Generic.TargetShape.FromCachedColumns"/> for the three MERGE/delete-insert writers
/// (<see cref="MsSqlDeleteInsertWriter"/>, <see cref="MsSqlMergeWriter"/>,
/// <see cref="MsSqlMergeReconcileWriter"/>) — see <c>TargetShapeCacheTests</c> in
/// <c>DataSync.Drivers.Generic.Tests</c> for the shared reasoning. Restated here rather than skipped
/// because the two types are genuinely separate implementations, not one shared by both drivers.
/// </summary>
public sealed class MsSqlTargetShapeCacheTests
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
        var shape = MsSqlTargetShape.FromCachedColumns("orders", TargetColumns, Target(), Mappings);

        Assert.Equal(["Id", "Amount"], shape.MappedTargetColumns);
        Assert.Equal(["Id"], shape.PrimaryKeyColumns);
        Assert.False(shape.RequiresIdentityInsert);
        Assert.Equal(3, shape.Columns.Count);
    }

    [Fact]
    public void AMappedIdentityColumn_RequiresIdentityInsert()
    {
        List<CachedColumn> withIdentity =
        [
            new("Id", "int", false, true, true),
            new("Amount", "decimal(18,2)", false, false, false),
        ];

        var shape = MsSqlTargetShape.FromCachedColumns("orders", withIdentity, Target(), Mappings);

        Assert.True(shape.RequiresIdentityInsert);
    }

    [Fact]
    public void AnEmptyCache_Throws()
    {
        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            MsSqlTargetShape.FromCachedColumns("orders", [], Target(), Mappings));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Null(ex.Column);
    }

    [Fact]
    public void ACacheMissingAMappedColumn_ThrowsNamingIt()
    {
        List<CachedColumn> incomplete = [new("Id", "int", false, true, false)];

        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            MsSqlTargetShape.FromCachedColumns("orders", incomplete, Target(), Mappings));

        Assert.Equal("Amount", ex.Column);
    }
}
