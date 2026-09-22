using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Phase 118's additions to <c>GET /api/drivers</c>: <c>library</c> and <c>capabilities</c> on
/// every row, and a real three-way <c>source</c> ("builtin" | "descriptor" | "compiled") instead of the
/// old builtin/descriptor guess. Two of the three cases install a real library or publish the compiled
/// test fixture, so the whole class is Integration even though the built-in case alone needs neither.</summary>
[Trait("Category", "Integration")]
public sealed class DriversControllerTests
{
    private sealed record CapabilitiesDto(List<string> Readers, List<string> Staging, List<string> Writers);
    private sealed record DriverDto(string Id, string DisplayName, bool BuiltIn, string Source, string? Library, CapabilitiesDto Capabilities);

    [Fact]
    public async Task BuiltInDrivers_ReportSourceBuiltin_AndNullLibrary()
    {
        using var factory = new TestApiFactory();
        var client = factory.CreateClient();

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");

        var mssql = drivers!.Single(d => d.Id == DriverIds.MsSql);
        Assert.True(mssql.BuiltIn);
        Assert.Equal("builtin", mssql.Source);
        Assert.Null(mssql.Library);
        Assert.NotEmpty(mssql.Capabilities.Readers);
        Assert.NotEmpty(mssql.Capabilities.Writers);
    }

    [Fact]
    public async Task ADescriptorDriver_ReportsItsLibrary_AndSourceDescriptor()
    {
        using var factory = new LibrariesAdminApiFactoryNoAuth();
        var client = factory.CreateClient();

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");

        var mysqlGeneric = drivers!.Single(d => d.Id == LibrariesAdminApiFactory.DriverId);
        Assert.False(mysqlGeneric.BuiltIn);
        Assert.Equal("descriptor", mysqlGeneric.Source);
        Assert.Equal(LibrariesAdminApiFactory.LibraryId, mysqlGeneric.Library);
        Assert.Contains("Watermark", mysqlGeneric.Capabilities.Readers);
    }

    [Fact]
    public async Task ACompiledPlugin_ReportsSourceCompiled()
    {
        using var factory = new CompiledDriverApiFactory();
        var client = factory.CreateClient();

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");

        var fixtureDriver = drivers!.Single(d => d.Id == CompiledDriverApiFactory.DriverId);
        Assert.False(fixtureDriver.BuiltIn);
        Assert.Equal("compiled", fixtureDriver.Source);
        Assert.Null(fixtureDriver.Library);
    }
}

/// <summary>The driver-authoring UI's own <c>GET /api/known-driver-kinds</c> — real assertions that it
/// reports exactly what <see cref="GenericDriverBase{TSpec}"/> itself accepts, not a copy that could
/// drift, and that it's admin-gated like every other Drivers/Libraries mutation.</summary>
[Trait("Category", "Integration")]
public sealed class DriverKnownKindsTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    private sealed record KindsDto(List<string> Readers, List<string> Staging, List<string> Writers);

    [Fact]
    public async Task AViewer_IsRefused()
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.GetAsync("/api/known-driver-kinds");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_SeesExactlyWhatGenericDriverBaseAccepts()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var kinds = await client.GetFromJsonAsync<KindsDto>("/api/known-driver-kinds");

        Assert.Equal(GenericDriverBase<GenericDriverSpec>.SupportedReaderKinds.OrderBy(x => x), kinds!.Readers.OrderBy(x => x));
        Assert.Equal(GenericDriverBase<GenericDriverSpec>.SupportedStagingKinds.OrderBy(x => x), kinds.Staging.OrderBy(x => x));
        Assert.Equal(GenericDriverBase<GenericDriverSpec>.SupportedWriterKinds.OrderBy(x => x), kinds.Writers.OrderBy(x => x));
        // Found missing, then fixed (GenericDriverKindCatalogTests.KeyReconcileScd2Close_IsAGenericWriterKind
        // pins the fix at the unit level) — asserted present here too, so this endpoint-level test would
        // itself catch a future regression, not just repeat the unit test's own assertion.
        Assert.Contains("KeyReconcileScd2Close", kinds.Writers);
    }
}
