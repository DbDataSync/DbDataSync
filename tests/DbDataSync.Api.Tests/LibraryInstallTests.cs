using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 120's web-triggered install/remove — real `dotnet publish` calls against the public feed, so
/// every test here is <c>Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibraryInstallTests : IDisposable
{
    private readonly AuthenticatedApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record PackageDto(string Id, string Version);
    private sealed record LibraryDto(string Id, List<PackageDto> Packages, string FactoryType, bool Resolves, List<string> UsedBy, bool Curated);
    private sealed record DriverDto(string Id, string DisplayName, bool BuiltIn, string Source, string? Library);
    private sealed record FromCatalogResultDto(string Id, string Library);

    /// <summary>The parent doc's own canary: a real, maintained provider with no container
    /// dependency.</summary>
    [Fact]
    public async Task InstallThenList_ShowsIt_Resolving_AndDeleteRemovesIt()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);

        var install = await client.PostAsJsonAsync("/api/libraries", new
        {
            packageId = "Microsoft.Data.Sqlite",
            version = "9.0.0",
        });
        install.EnsureSuccessStatusCode();

        var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");
        var library = libraries!.Single(l => l.Id == "Microsoft.Data.Sqlite");
        Assert.True(library.Resolves);
        Assert.Contains("Microsoft.Data.Sqlite", library.Packages.Select(p => p.Id));

        var delete = await client.DeleteAsync("/api/libraries/Microsoft.Data.Sqlite");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var afterDelete = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");
        Assert.DoesNotContain(afterDelete!, l => l.Id == "Microsoft.Data.Sqlite");
    }

    /// <summary>A real, restorable package with no <c>DbProviderFactory</c> subclass at all — phase
    /// 122's reflection-assist restores it, finds nothing, and only then answers 400. Previously this
    /// package id did not even exist on NuGet, so the 400 came from a pre-restore check that no longer
    /// runs; this proves the *post-restore* failure path still ends in a clear 400 instead of a 500
    /// from the restore itself succeeding with nothing to show for it.</summary>
    [Fact]
    public async Task InstallWithNoFactoryTypeAndNoneKnown_Returns400NamingTheField()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/libraries", new
        {
            packageId = "Newtonsoft.Json",
            version = "13.0.3",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DbProviderFactory", body);

        var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");
        Assert.DoesNotContain(libraries!, l => l.Id == "Newtonsoft.Json");
    }

    /// <summary>A real ADO.NET provider deliberately not one of the bundled catalog's entries — phase
    /// 122's acceptance criterion: no <c>factoryType</c>, no catalog match, and the install still
    /// completes because reflection-assist found the one candidate in the restored closure.</summary>
    [Fact]
    public async Task InstallWithNoFactoryTypeForANonCatalogPackage_DiscoversItByReflection()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/libraries", new
        {
            packageId = "System.Data.SqlClient",
            version = "4.9.0",
        });

        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<LibraryDto>();
        Assert.Equal("System.Data.SqlClient.SqlClientFactory, System.Data.SqlClient", manifest!.FactoryType);

        var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");
        var library = libraries!.Single(l => l.Id == "System.Data.SqlClient");
        Assert.True(library.Resolves);
        Assert.False(library.Curated);
    }

    [Fact]
    public async Task AViewer_IsRefusedByAllThreeMutations()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Viewer);

        var post = await client.PostAsJsonAsync("/api/libraries", new { packageId = "x", version = "1.0.0" });
        var delete = await client.DeleteAsync("/api/libraries/x");
        var fromCatalog = await client.PostAsJsonAsync("/api/drivers/from-catalog", new { knownDriverId = "mysql.generic", version = "2.4.0" });

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fromCatalog.StatusCode);
    }

    [Fact]
    public async Task FromCatalog_InstallsTheBoundLibrary_AndWritesTheDescriptor()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/drivers/from-catalog", new
        {
            knownDriverId = "mysql.generic",
            version = "2.4.0",
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<FromCatalogResultDto>();
        Assert.Equal("mysql.generic", result!.Id);
        Assert.Equal("mysql-connector", result.Library);

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");
        var driver = drivers!.Single(d => d.Id == "mysql.generic");
        Assert.Equal("descriptor", driver.Source);
        Assert.Equal("mysql-connector", driver.Library);

        var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/libraries");
        var library = libraries!.Single(l => l.Id == "mysql-connector");
        Assert.Contains("mysql.generic", library.UsedBy);
    }

    [Fact]
    public async Task FromCatalog_RefusesWhenADriverWithThatIdAlreadyExists()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);
        var request = new { knownDriverId = "mysql.generic", version = "2.4.0" };
        (await client.PostAsJsonAsync("/api/drivers/from-catalog", request)).EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/drivers/from-catalog", request);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task DeleteWhileADriverStillBindsIt_Refuses_ForceRemovesIt_AndTheDriverThenFailsToLoad()
    {
        var client = await _factory.SignedInAsAsync(UserRole.Admin);
        (await client.PostAsJsonAsync("/api/drivers/from-catalog", new
        {
            knownDriverId = "mysql.generic",
            version = "2.4.0",
        })).EnsureSuccessStatusCode();

        var refused = await client.DeleteAsync("/api/libraries/mysql-connector");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.Contains("mysql.generic", refusedBody);

        var forced = await client.DeleteAsync("/api/libraries/mysql-connector?force=true");
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);

        // A fresh host over the same repo root — this process never hot-reloads a removed library, so
        // the only way to observe "the driver now fails to load" is a new composition root, same as a
        // real restart. Host startup itself must still survive (the existing onError contract).
        using var restarted = new TestApiFactoryOnRepo(_factory.RepoRoot);
        var restartedClient = restarted.CreateClient();
        var drivers = await restartedClient.GetFromJsonAsync<List<DriverDto>>("/api/drivers");
        Assert.DoesNotContain(drivers!, d => d.Id == "mysql.generic");
    }
}
