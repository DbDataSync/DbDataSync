using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>The driver-authoring form's own backend: <c>POST /api/drivers</c>,
/// <c>GET</c>/<c>PUT /api/drivers/{id}/yaml</c>. Both mutations validate entirely in memory before
/// touching disk — these tests pin that a failed validation never leaves a directory behind, not just
/// that it returns the right status code.</summary>
[Trait("Category", "Integration")]
public sealed class DriverAuthoringTests(DriverAuthoringApiFactory factory) : IClassFixture<DriverAuthoringApiFactory>
{
    private sealed record YamlDto(string Yaml);
    private sealed record DriverDto(string Id, string DisplayName);

    private static string ValidYaml(string id) => $"""
        id: {id}
        displayName: Authoring test driver
        library: {DriverAuthoringApiFactory.LibraryId}
        dialect:
          quoteIdentifier: backtick
          parameterPrefix: "@"
          rowLimit: limitOffset
        typeMap:
          int: Int32
        capabilities:
          readers: [Watermark]
          staging: [StagingTable]
          writers: [DeleteInsert]
        """;

    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "/some-id/yaml")]
    [InlineData("PUT", "/some-id/yaml")]
    public async Task AViewer_IsRefused(string method, string suffix)
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/drivers{suffix}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithValidYaml_RegistersTheDriver_AndAppearsInList()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-create-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");
        Assert.Contains(drivers!, d => d.Id == id);
    }

    [Fact]
    public async Task Create_WithADuplicateId_Returns409()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-dup-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        var response = await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidYaml_Returns400_AndWritesNothing()
    {
        // Before/after count, not outright absence — the fixture's RepoRoot is shared across every test
        // in this class, so a sibling test's own successful create may already have a drivers/ directory
        // by the time this one runs. Unparseable YAML has no id to name a specific path with, so "no new
        // directory appeared" is the strongest thing this case alone can prove.
        var driversDir = Path.Combine(factory.RepoRoot, "drivers");
        var before = Directory.Exists(driversDir) ? Directory.GetDirectories(driversDir).Length : 0;
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/drivers", new { yaml = "not: [valid: yaml: at all" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid YAML", body);
        var after = Directory.Exists(driversDir) ? Directory.GetDirectories(driversDir).Length : 0;
        Assert.Equal(before, after);
    }

    /// <summary>The regression this whole validate-in-memory design exists to catch cleanly:
    /// `DriverDescriptorReader.BuildDriver`'s JDBC path dispatches through `MethodInfo.Invoke`, which
    /// wraps whatever `JdbcGenericDriver.FromDescriptor` itself throws in a `TargetInvocationException`.
    /// Without unwrapping it, this request would 400 with a generic reflection-wrapper message instead
    /// of `FromDescriptor`'s own real one — and, separately, no `drivers/&lt;id&gt;/` directory should
    /// exist afterward, proving validation genuinely ran before any write.</summary>
    [Fact]
    public async Task Create_WithAJdbcBaseMissingItsJdbcBlock_ReturnsTheRealError_NotAReflectionWrapper()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-jdbc-bad-{Guid.NewGuid():N}";
        var yaml = """
            id: __ID__
            displayName: Broken JDBC driver
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              rowLimit: limitOffset
            typeMap: {}
            capabilities:
              readers: []
              staging: []
              writers: []
            """.Replace("__ID__", id);

        var response = await client.PostAsJsonAsync("/api/drivers", new { yaml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("requires a jdbc block", body);
        Assert.DoesNotContain("TargetInvocationException", body);
        Assert.False(Directory.Exists(Path.Combine(factory.RepoRoot, "drivers", id)));
    }

    [Fact]
    public async Task GetYaml_ForAnExistingDriver_ReturnsTheRawText()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-get-{Guid.NewGuid():N}";
        var yaml = ValidYaml(id);
        await client.PostAsJsonAsync("/api/drivers", new { yaml });

        var loaded = await client.GetFromJsonAsync<YamlDto>($"/api/drivers/{id}/yaml");

        Assert.Equal(yaml, loaded!.Yaml);
    }

    [Fact]
    public async Task GetYaml_ForAMissingDriver_Returns404()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.GetAsync("/api/drivers/does-not-exist/yaml");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateYaml_WithAValidChange_Persists()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-update-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        var updated = $"""
            id: {id}
            displayName: Renamed display name
            library: {DriverAuthoringApiFactory.LibraryId}
            dialect:
              quoteIdentifier: backtick
              parameterPrefix: "@"
              rowLimit: limitOffset
            typeMap:
              int: Int32
            capabilities:
              readers: [Watermark]
              staging: [StagingTable]
              writers: [DeleteInsert]
            """;

        var response = await client.PutAsJsonAsync($"/api/drivers/{id}/yaml", new { yaml = updated });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var drivers = await client.GetFromJsonAsync<List<DriverDto>>("/api/drivers");
        Assert.Contains(drivers!, d => d.Id == id && d.DisplayName == "Renamed display name");
    }

    [Fact]
    public async Task UpdateYaml_ForAMissingDriver_Returns404()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PutAsJsonAsync("/api/drivers/does-not-exist/yaml", new { yaml = ValidYaml("does-not-exist") });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateYaml_WhoseOwnIdDoesNotMatchTheUrl_Returns400()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"authoring-mismatch-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        var response = await client.PutAsJsonAsync($"/api/drivers/{id}/yaml", new { yaml = ValidYaml("a-different-id") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
