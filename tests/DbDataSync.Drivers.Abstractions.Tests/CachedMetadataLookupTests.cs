using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Abstractions.Tests;

/// <summary>
/// The one piece of logic every one of phase 91's six run-time consumers routes through: look a column
/// up by name in a mapping's cached shape, or throw <see cref="MetadataNotCachedException"/> naming the
/// mapping, the side and — when there is one — the column. Tested once, here, rather than six times
/// against six readers/writers, because this is the part that actually decides "cached" vs. "not".
/// </summary>
public sealed class CachedMetadataLookupTests
{
    private static readonly CachedColumn Id = new("Id", "int", false, true, false);
    private static readonly CachedColumn UpdatedAt = new("UpdatedAt", "datetime2", true, false, false);

    [Fact]
    public void RequireColumn_FindsItCaseInsensitively_AndConvertsEveryFact()
    {
        var found = ((IReadOnlyList<CachedColumn>)[Id, UpdatedAt])
            .RequireColumn("orders", "source", "updatedat");

        Assert.Equal("UpdatedAt", found.Name);
        Assert.Equal("datetime2", found.NativeType);
        Assert.True(found.IsNullable);
        Assert.False(found.IsPrimaryKey);
        Assert.False(found.IsIdentity);
    }

    [Fact]
    public void RequireColumn_AnEmptyCache_ThrowsNamingTheMappingAndSideButNoColumn()
    {
        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            ((IReadOnlyList<CachedColumn>)[]).RequireColumn("orders", "source", "UpdatedAt"));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("source", ex.Side);
        Assert.Null(ex.Column);
        Assert.Contains("orders", ex.Message);
        Assert.Contains("Refresh metadata", ex.Message);
    }

    [Fact]
    public void RequireColumn_APopulatedCacheMissingTheColumn_ThrowsNamingIt()
    {
        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            ((IReadOnlyList<CachedColumn>)[Id]).RequireColumn("orders", "target", "UpdatedAt"));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Equal("UpdatedAt", ex.Column);
        Assert.Contains("UpdatedAt", ex.Message);
        Assert.Contains("Refresh metadata", ex.Message);
    }

    [Fact]
    public void RequireAll_APopulatedCache_ConvertsEveryColumnInOrder()
    {
        var all = ((IReadOnlyList<CachedColumn>)[Id, UpdatedAt]).RequireAll("orders", "source");

        Assert.Equal(["Id", "UpdatedAt"], all.Select(c => c.Name));
        Assert.True(all[0].IsPrimaryKey);
    }

    [Fact]
    public void RequireAll_AnEmptyCache_Throws()
    {
        var ex = Assert.Throws<MetadataNotCachedException>(() =>
            ((IReadOnlyList<CachedColumn>)[]).RequireAll("orders", "target"));

        Assert.Equal("orders", ex.MappingName);
        Assert.Equal("target", ex.Side);
        Assert.Null(ex.Column);
    }

    [Fact]
    public void ToColumnMetadata_CarriesAllFiveFacts()
    {
        var converted = Id.ToColumnMetadata();

        Assert.Equal(Id.Name, converted.Name);
        Assert.Equal(Id.NativeType, converted.NativeType);
        Assert.Equal(Id.IsNullable, converted.IsNullable);
        Assert.Equal(Id.IsPrimaryKey, converted.IsPrimaryKey);
        Assert.Equal(Id.IsIdentity, converted.IsIdentity);
    }
}
