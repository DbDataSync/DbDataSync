using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// Phase 122's reflection-assist, exercised against three real published fixture assemblies —
/// <c>tests/fixtures/DbDataSync.Libraries.FactoryFixture*</c> — rather than a hand-rolled in-memory
/// assembly, so this proves the real <see cref="System.Reflection.MetadataLoadContext"/> path against
/// real files on disk, the same shape a restored library's <c>lib/</c> directory has.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactoryTypeReflectorTests
{
    [Fact]
    public void Discover_ASinglePublicNonAbstractSubclassWithAnInstanceMember_IsFound()
    {
        var libDir = FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.FactoryFixtureOne");

        var result = FactoryTypeReflector.Discover(libDir);

        Assert.Equal(FactoryTypeDiscoveryStatus.Found, result.Status);
        Assert.Equal("DbDataSync.Libraries.FactoryFixtureOne.GoodFactory, DbDataSync.Libraries.FactoryFixtureOne", result.FactoryType);
    }

    [Fact]
    public void Discover_TwoQualifyingSubclasses_IsAmbiguous()
    {
        var libDir = FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.FactoryFixtureAmbiguous");

        var result = FactoryTypeReflector.Discover(libDir);

        Assert.Equal(FactoryTypeDiscoveryStatus.Ambiguous, result.Status);
        Assert.Null(result.FactoryType);
    }

    [Fact]
    public void Discover_NoQualifyingType_IsNotFound()
    {
        var libDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-factory-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(libDir);
        try
        {
            var result = FactoryTypeReflector.Discover(libDir);

            Assert.Equal(FactoryTypeDiscoveryStatus.NotFound, result.Status);
            Assert.Null(result.FactoryType);
        }
        finally
        {
            Directory.Delete(libDir, recursive: true);
        }
    }

    /// <summary>The scan is inspection-only: this fixture's module initializer throws the moment the
    /// assembly is actually loaded (see its own doc comment), so a passing result here — rather than
    /// the thrown exception bubbling out of this test — is the proof that
    /// <see cref="FactoryTypeReflector.Discover"/> never runs anything in what it scans.</summary>
    [Fact]
    public void Discover_DoesNotExecuteAModuleInitializer()
    {
        var libDir = FixturePublisher.OutputDirectoryFor("DbDataSync.Libraries.FactoryFixtureModuleInit");

        var result = FactoryTypeReflector.Discover(libDir);

        Assert.Equal(FactoryTypeDiscoveryStatus.Found, result.Status);
        Assert.Equal(
            "DbDataSync.Libraries.FactoryFixtureModuleInit.ThrowingFactory, DbDataSync.Libraries.FactoryFixtureModuleInit",
            result.FactoryType);
    }

    [Fact]
    public void Discover_ADirectoryWithNoDlls_IsNotFound()
    {
        var libDir = Path.Combine(Path.GetTempPath(), $"dbdatasync-factory-missing-{Guid.NewGuid():N}");

        var result = FactoryTypeReflector.Discover(libDir);

        Assert.Equal(FactoryTypeDiscoveryStatus.NotFound, result.Status);
    }
}
