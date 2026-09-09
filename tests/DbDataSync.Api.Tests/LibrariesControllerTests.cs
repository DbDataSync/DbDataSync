using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 118's read-only Libraries screen: installed libraries, and the bundled catalogs an "add"
/// affordance offers. Admin-only, per <see cref="LibrariesController"/>'s own doc comment.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibrariesControllerTests(LibrariesAdminApiFactory factory) : IClassFixture<LibrariesAdminApiFactory>
{
    private sealed record PackageDto(string Id, string Version);
    private sealed record LibraryDto(
        string Id, List<PackageDto> Packages, string FactoryType, bool Resolves, List<string> UsedBy, bool Curated);
    private sealed record KnownLibraryDto(string Id, string DisplayName, string Description, string PackageId);
    private sealed record KnownDriverDto(string Id, string DisplayName, string Description, string BoundLibrary);

    [Theory]
    [InlineData("/api/libraries")]
    [InlineData("/api/known-libraries")]
    [InlineData("/api/known-drivers")]
    public async Task AViewer_IsRefused(string path)
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_SeesTheInstalledLibrary_WithUsedByPopulatedFromTheRealDescriptor()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");

        var library = libraries!.Single(l => l.Id == LibrariesAdminApiFactory.LibraryId);
        Assert.Equal("MySqlConnector.MySqlConnectorFactory, MySqlConnector", library.FactoryType);
        Assert.Contains("MySqlConnector", library.Packages.Select(p => p.Id));
        Assert.True(library.Resolves);
        Assert.Contains(LibrariesAdminApiFactory.DriverId, library.UsedBy);
        Assert.True(library.Curated);
    }

    [Fact]
    public async Task AnAdmin_SeesTheBundledCatalogs()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var libraries = await client.GetFromJsonAsync<List<KnownLibraryDto>>("/api/known-libraries");
        Assert.Contains(libraries!, l => l.Id == "mysql-connector" && l.PackageId == "MySqlConnector");

        var drivers = await client.GetFromJsonAsync<List<KnownDriverDto>>("/api/known-drivers");
        Assert.Contains(drivers!, d => d.Id == "mysql.generic" && d.BoundLibrary == "mysql-connector");
    }
}
