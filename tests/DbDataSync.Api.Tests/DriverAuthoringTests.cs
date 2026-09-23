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

/// <summary>Phase 181N: <c>POST /api/drivers/validate</c> — validate-and-echo, entirely in memory, never
/// writing to disk (proven directly, not just inferred from the shared "validate before write" design
/// the rest of this file already pins for <c>Create</c>/<c>UpdateYaml</c>).</summary>
[Trait("Category", "Integration")]
public sealed class DriverValidateTests(DriverAuthoringApiFactory factory) : IClassFixture<DriverAuthoringApiFactory>
{
    private sealed record ConnectionStringKeysDto(
        string Host, string? Port, string Database, string Username, string Password,
        string? ConnectTimeout, string? IntegratedSecurity);
    private sealed record DialectDto(
        string QuoteIdentifier, string ParameterPrefix, string RowLimit, string Catalog,
        string DefaultDatabase, int? DefaultPort, ConnectionStringKeysDto ConnectionStringKeys);
    private sealed record JdbcDto(
        string DriverClass, List<string> DriverJarPaths, string? UrlTemplate, ConnectionStringKeysDto ConnectionStringKeys);
    private sealed record InterpretedDto(
        string Id, string DisplayName, string Base, DialectDto Dialect, Dictionary<string, string> TypeMap,
        List<string> Readers, List<string> Staging, List<string> Writers, JdbcDto? Jdbc);
    private sealed record ValidationDto(bool Valid, string? Error, InterpretedDto? Interpreted);

    [Fact]
    public async Task AViewer_CanValidate()
    {
        // Unlike Create/UpdateYaml (Admin), Validate is Viewer — matching every other endpoint
        // DriverEditPage/ConnectionEditPage already call at that level.
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml = ValidYaml("validate-viewer") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithValidYaml_ReportsValid_AndEchoesTheInterpretedShape()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml = ValidYaml("validate-ok") });

        var result = await response.Content.ReadFromJsonAsync<ValidationDto>();
        Assert.True(result!.Valid);
        Assert.Null(result.Error);
        Assert.Equal("validate-ok", result.Interpreted!.Id);
        Assert.Equal("adonet", result.Interpreted.Base);
        Assert.Equal("information_schema", result.Interpreted.Dialect.Catalog);
        Assert.Equal("Int32", result.Interpreted.TypeMap["int"]);
        Assert.Equal(["Watermark"], result.Interpreted.Readers);
        Assert.Null(result.Interpreted.Jdbc);
    }

    [Fact]
    public async Task WithAJdbcUrlTemplateAndConnectionStringKeys_EchoesThemResolved()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var yaml = """
            id: validate-jdbc
            displayName: Validate JDBC
            library: ikvm
            base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
            jdbc:
              driverClass: org.postgresql.Driver
              driverJarPaths: [postgresql.jar]
              urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"
              connectionStringKeys:
                username: pguser
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              parameterNameIsBare: true
              rowLimit: limitOffset
            typeMap: {}
            capabilities:
              readers: []
              staging: []
              writers: []
            """;

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml });

        var result = await response.Content.ReadFromJsonAsync<ValidationDto>();
        Assert.Equal("jdbc", result!.Interpreted!.Base);
        Assert.Equal("java.sql.DatabaseMetaData", result.Interpreted.Dialect.Catalog);
        Assert.Equal("jdbc:postgresql://{host}:{port}/{database}", result.Interpreted.Jdbc!.UrlTemplate);
        Assert.Equal("pguser", result.Interpreted.Jdbc.ConnectionStringKeys.Username);
        // Not overridden by the yaml — still JDBC's own default spelling, not left blank/null.
        Assert.Equal("host", result.Interpreted.Jdbc.ConnectionStringKeys.Host);
    }

    /// <summary>The case this endpoint exists to handle specially: a yaml that parses but fails to
    /// *build* still gets an interpreted echo (built from the parsed descriptor, not the never-built
    /// driver) alongside the real error — "here's what I understood before I hit a problem".</summary>
    [Fact]
    public async Task WithAYamlThatParsesButFailsToBuild_ReportsInvalid_ButStillEchoesWhatItUnderstood()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var yaml = """
            id: validate-broken-jdbc
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
            """;

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml });

        var result = await response.Content.ReadFromJsonAsync<ValidationDto>();
        Assert.False(result!.Valid);
        Assert.Contains("requires a jdbc block", result.Error);
        Assert.Equal("validate-broken-jdbc", result.Interpreted!.Id);
        Assert.Equal("jdbc", result.Interpreted.Base);
    }

    [Fact]
    public async Task WithUnparseableYaml_ReportsInvalid_WithNoInterpretedEcho()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml = "not: [valid: yaml: at all" });

        var result = await response.Content.ReadFromJsonAsync<ValidationDto>();
        Assert.False(result!.Valid);
        Assert.Contains("Invalid YAML", result.Error);
        Assert.Null(result.Interpreted);
    }

    [Fact]
    public async Task NeverWritesToDisk_EvenForAValidYaml()
    {
        var driversDir = Path.Combine(factory.RepoRoot, "drivers");
        var before = Directory.Exists(driversDir) ? Directory.GetDirectories(driversDir).Length : 0;
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"validate-no-write-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/drivers/validate", new { yaml = ValidYaml(id) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(driversDir, id)));
        var after = Directory.Exists(driversDir) ? Directory.GetDirectories(driversDir).Length : 0;
        Assert.Equal(before, after);
    }

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
}

/// <summary>Phase 182N: <c>GET /api/drivers/{id}/status</c> — what `ConnectionEditPage` calls to explain
/// *why* a connection's driver isn't registered, instead of a silently empty parameter form.</summary>
[Trait("Category", "Integration")]
public sealed class DriverStatusTests(DriverAuthoringApiFactory factory) : IClassFixture<DriverAuthoringApiFactory>
{
    private sealed record StatusDto(bool Registered, string? Error);

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

    [Fact]
    public async Task AViewer_CanCheckStatus()
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.GetAsync("/api/drivers/does-not-exist/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARegisteredDriver_ReportsRegistered_WithNoError()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"status-registered-{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/drivers", new { yaml = ValidYaml(id) });

        var status = await client.GetFromJsonAsync<StatusDto>($"/api/drivers/{id}/status");

        Assert.True(status!.Registered);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task ADirectoryThatDoesNotExist_ReportsANoDriverYamlError()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var status = await client.GetFromJsonAsync<StatusDto>("/api/drivers/does-not-exist/status");

        Assert.False(status!.Registered);
        Assert.Contains("No driver.yaml exists", status.Error);
    }

    /// <summary>The real case this endpoint exists for: a `driver.yaml` on disk that failed to load at
    /// startup (never registered), where the error matches what `Create`/`UpdateYaml`/`Validate` would
    /// have shown for the identical file — not a fourth, differently-worded message.</summary>
    [Fact]
    public async Task ADescriptorThatFailsToBuild_ReportsTheSameErrorValidateWould()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var id = $"status-broken-{Guid.NewGuid():N}";
        var brokenYaml = """
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
        // Written directly to disk, bypassing Create (which would refuse this yaml) — this is exactly
        // the "already on disk, never registered" state this endpoint exists to explain.
        var driverDir = Path.Combine(factory.RepoRoot, "drivers", id);
        Directory.CreateDirectory(driverDir);
        await File.WriteAllTextAsync(Path.Combine(driverDir, "driver.yaml"), brokenYaml);

        var status = await client.GetFromJsonAsync<StatusDto>($"/api/drivers/{id}/status");

        Assert.False(status!.Registered);
        Assert.Contains("requires a jdbc block", status.Error);
    }
}
