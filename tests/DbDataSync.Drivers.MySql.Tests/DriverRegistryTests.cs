using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.MySql.Tests;

// Pure in-memory test of DriverRegistry against a real IDriver (MySqlDriver) — no server needed.
public sealed class DriverRegistryTests
{
    [Fact]
    public void Describe_ReflectsMySqlsOwnLackOfATieSafeRowLimit()
    {
        // MySqlDialect's RenderTieSafeRowLimit admits it isn't actually tie-safe (a plain LIMIT n, no
        // WITH TIES equivalent) — this is what a UI would check before warning about maxRowsPerRead.
        var registry = new DriverRegistry();
        registry.Register(new MySqlDriver());

        var capabilities = registry.Describe(DriverIds.MySql);

        Assert.NotNull(capabilities);
        Assert.False(capabilities.SupportsTieSafeRowLimit);
    }
}
