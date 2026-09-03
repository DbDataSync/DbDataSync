using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// What a connection takes now depends on what it has been given so far, which is why this is a POST
/// carrying values rather than a field on the cacheable capabilities response. The rule lives with the
/// driver that owns the setting — the alternative is a condition language in the SPA that has to agree
/// about what "SqlAuth" implies, and that is the copy that would be wrong.
/// </summary>
public sealed class ConnectionParametersTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<List<ParameterDescriptor>> AskAsync(string driver, object values)
    {
        var response = await _client.PostAsJsonAsync($"/api/drivers/{driver}/connection-parameters", values, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<ParameterDescriptor>>(JsonOptions))!;
    }

    private static ParameterDescriptor Find(List<ParameterDescriptor> parameters, string name) =>
        Assert.Single(parameters, p => p.Name == name);

    /// <summary>An empty bag is the question a new connection's form asks, so it has to answer as a
    /// fresh connection would rather than as a connection with no addressing mode at all.</summary>
    [Fact]
    public async Task WithNoValues_TheDefaultsApply()
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string>());

        Assert.True(Find(parameters, "host").Visible);
        Assert.True(Find(parameters, "port").Visible);
        Assert.False(Find(parameters, "connectionString").Visible);
        Assert.True(Find(parameters, "userId").Visible);
        Assert.True(Find(parameters, "password").Visible);
    }

    [Fact]
    public async Task ConnectionStringAddressing_HidesHostAndPort()
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string> { ["addressMode"] = "connectionString" });

        Assert.False(Find(parameters, "host").Visible);
        Assert.False(Find(parameters, "port").Visible);
        Assert.True(Find(parameters, "connectionString").Visible);
    }

    [Theory]
    [InlineData("IntegratedAuth")]
    [InlineData("None")]
    public async Task AnAuthModeThatSuppliesNoCredential_HidesUserAndPassword(string authMode)
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string> { ["authMode"] = authMode });

        Assert.False(Find(parameters, "userId").Visible);
        Assert.False(Find(parameters, "password").Visible);
    }

    /// <summary>
    /// Only the two settings anything depends on ask the form to come back. A parameter with no
    /// dependents saying <c>recalc</c> would mean a round trip per keystroke in a host field.
    /// </summary>
    [Fact]
    public async Task OnlyTheTwoDependedOnSettings_AskForARecompute()
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string>());

        Assert.Equal(["addressMode", "authMode"], parameters.Where(p => p.Recalc).Select(p => p.Name).Order());
    }

    /// <summary>The client used to hold a table of these, which a third driver would have made stale
    /// the day it was added.</summary>
    [Theory]
    [InlineData("MsSql", "1433")]
    [InlineData("Postgres", "5432")]
    public async Task EachDriverDeclaresItsOwnDefaultPort(string driver, string expected)
    {
        var parameters = await AskAsync(driver, new Dictionary<string, string>());

        Assert.Equal(expected, Find(parameters, "port").Default);
    }

    /// <summary>Declared as a secret so the form masks it and the "blank keeps the stored one"
    /// contract belongs to the type rather than to one hand-written field.</summary>
    [Fact]
    public async Task ThePassword_IsDeclaredASecret()
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string>());

        Assert.Equal(ParameterType.Secret, Find(parameters, "password").Type);
    }

    /// <summary>A connection's name is its identity and its driver is the selector that decides which
    /// set applies — neither is one of the settings in the set.</summary>
    [Fact]
    public async Task NameAndDriver_AreNotDeclaredSettings()
    {
        var parameters = await AskAsync("MsSql", new Dictionary<string, string>());

        Assert.DoesNotContain(parameters, p => p.Name is "name" or "driverType");
        Assert.Contains(parameters, p => p.Name == "properties");
    }

    [Fact]
    public async Task AnUnregisteredDriver_IsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/drivers/MsSql/connection-parameters", new Dictionary<string, string>(), JsonOptions);
        response.EnsureSuccessStatusCode();

        var missing = await _client.PostAsJsonAsync(
            "/api/drivers/NotADriver/connection-parameters", new Dictionary<string, string>(), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}
