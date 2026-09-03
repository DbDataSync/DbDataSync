using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

// Pure in-memory tests of DriverRegistry against a real IDriver (MsSqlDriver) — no SQL Server needed,
// unlike the rest of this project, so these are not tagged Category=Integration.
public sealed class DriverRegistryTests
{
    [Fact]
    public void Register_ThenGet_ReturnsSameDriver()
    {
        var registry = new DriverRegistry();
        var driver = new MsSqlDriver();
        registry.Register(driver);

        Assert.Same(driver, registry.Get(ConnectionDriverType.MsSql));
    }

    [Fact]
    public void Get_WhenNotRegistered_Throws()
    {
        var registry = new DriverRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.Get(ConnectionDriverType.MsSql));
    }

    [Fact]
    public void TryGet_WhenNotRegistered_ReturnsFalse()
    {
        var registry = new DriverRegistry();
        Assert.False(registry.TryGet(ConnectionDriverType.MsSql, out _));
    }

    [Theory]
    [InlineData(MsSqlDriverKinds.ChangeTracking, true)]
    [InlineData(MsSqlDriverKinds.Watermark, true)]
    [InlineData("NotARealKind", false)]
    public void SupportsReader_ReflectsRegisteredDriverCapabilities(string kind, bool expected)
    {
        var registry = new DriverRegistry();
        registry.Register(new MsSqlDriver());

        Assert.Equal(expected, registry.SupportsReader(ConnectionDriverType.MsSql, kind));
    }

    [Fact]
    public void SupportsStagingProviderAndWriter_ReflectRegisteredCapabilities()
    {
        var registry = new DriverRegistry();
        registry.Register(new MsSqlDriver());

        Assert.True(registry.SupportsStagingProvider(ConnectionDriverType.MsSql, MsSqlDriverKinds.StagingTable));
        Assert.True(registry.SupportsWriter(ConnectionDriverType.MsSql, MsSqlDriverKinds.Merge));
        Assert.False(registry.SupportsWriter(ConnectionDriverType.MsSql, "Bogus"));
    }
}
