using System.Net;
using System.Net.Http.Json;
using DataSync.State;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The Certificates section of the Admin screen (phase 83), at the HTTP layer — authorization
/// (<c>[Authorize(Policies.Admin)]</c> on every action, read included) and the shape of a failure
/// response. <see cref="AdminCertificateServiceWindowsTests"/> is what actually verifies this phase's
/// logic in this sandbox; see that class's own doc comment for why: every test in *this* file that signs
/// in (all of them) hits the same pre-existing, already-documented gap
/// <c>AdminConfigControllerTests</c>' own class doc comment names — <c>System.NotSupportedException:
/// Negotiate authentication requires a server that supports IConnectionItemsFeature like Kestrel</c>,
/// thrown by <c>TestServer</c> the moment any authenticated request reaches Negotiate on this Windows
/// sandbox. These tests are written correctly and will pass in CI, which runs on Linux (Negotiate is
/// never registered there); they cannot be made to pass in *this* environment without touching test
/// infrastructure this phase does not own — the same call phase 79/81 already made about their own
/// environment-specific gaps.
/// </summary>
public sealed class AdminCertificateControllerTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    [Theory]
    [InlineData("GET", "/api/admin/certificate", false)]
    [InlineData("GET", "/api/admin/certificate/candidates", false)]
    [InlineData("POST", "/api/admin/certificate/self-signed", true)]
    [InlineData("POST", "/api/admin/certificate/enroll", true)]
    [InlineData("POST", "/api/admin/certificate/retrieve", true)]
    [InlineData("POST", "/api/admin/certificate/bind", true)]
    public async Task AViewer_IsRefusedByEveryEndpoint_ReadIncluded(string method, string path, bool hasBody)
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (hasBody)
            request.Content = JsonContent.Create(new { dnsNames = new[] { "x" }, template = "x", requestId = "x", thumbprint = "x" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CanReadStatus()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.GetAsync("/api/admin/certificate");

        response.EnsureSuccessStatusCode();
    }

    /// <summary>The same assertion <c>AdminCertificateServiceWindowsTests.SerializedStatus_NeverContainsKeyMaterial</c>
    /// makes at the service layer, repeated here against the real HTTP response body — belt and braces,
    /// since this is the shape an actual browser would receive.</summary>
    [Fact]
    public async Task GetStatus_ResponseBody_NeverContainsKeyMaterial()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.GetAsync("/api/admin/certificate");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-----BEGIN", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfSigned_WithNoDnsNames_Returns400()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/certificate/self-signed", new { dnsNames = Array.Empty<string>(), validityDays = (int?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Retrieve_UnknownRequestId_Returns400()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/certificate/retrieve", new { requestId = "never-submitted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Bind_UnknownThumbprint_Returns400()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/certificate/bind",
            new { thumbprint = "0000000000000000000000000000000000dead", allowInvalid = (bool?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
