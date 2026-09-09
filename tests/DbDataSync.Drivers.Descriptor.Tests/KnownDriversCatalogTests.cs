using System.Data.Common;
using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Drivers.Descriptor.Tests;

/// <summary>A bundled descriptor can't rot: every <see cref="KnownDrivers"/> entry has to keep parsing
/// and standing up a <see cref="Drivers.Generic.GenericDriverSpec"/>, and every catalog cross-reference
/// (a driver's bound library, a library's factory type) has to keep resolving syntactically — these
/// run against the actual embedded resources and catalog tables, not a copy of them.</summary>
public sealed class KnownDriversCatalogTests
{
    private sealed class StubDbProviderFactory : DbProviderFactory;

    public static IEnumerable<object[]> AllDrivers => KnownDrivers.All.Select(e => new object[] { e });

    public static IEnumerable<object[]> AllLibraries => KnownLibraries.All.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void EveryEntry_RoundTripsThroughTheDescriptorReader(KnownDriverEntry entry)
    {
        var yaml = KnownDrivers.Render(entry, entry.Id, entry.DisplayName, entry.BoundLibraryId);

        var descriptor = DriverDescriptorReader.Deserialize(yaml);
        Assert.Equal(entry.Id, descriptor.Id);
        Assert.Equal(entry.DisplayName, descriptor.DisplayName);
        Assert.Equal(entry.BoundLibraryId, descriptor.Library);

        var spec = DriverDescriptorReader.ToSpec(descriptor, new StubDbProviderFactory());
        Assert.Equal(entry.Id, spec.Id);
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void EveryEntrysBoundLibrary_ExistsInKnownLibraries(KnownDriverEntry entry)
    {
        Assert.NotNull(KnownLibraries.TryGetById(entry.BoundLibraryId));
    }

    [Theory]
    [MemberData(nameof(AllLibraries))]
    public void EveryLibrarysFactoryType_IsAWellFormedAssemblyQualifiedName(LibraryCatalogEntry entry)
    {
        var parts = entry.FactoryType.Split(',', 2);

        Assert.Equal(2, parts.Length);
        Assert.False(string.IsNullOrWhiteSpace(parts[0]));
        Assert.False(string.IsNullOrWhiteSpace(parts[1]));
    }
}
