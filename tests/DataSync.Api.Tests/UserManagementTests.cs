using System.Net;
using System.Net.Http.Json;
using DataSync.State;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// The guards that stop somebody locking everybody out of the tool that manages the lockout. Three
/// lines of check each, and a lost afternoon without them.
/// </summary>
public sealed class UserManagementTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    [Fact]
    public async Task AnAdminSeesWhoExists_AndHowTheySignIn()
    {
        var (client, user) = await factory.SignedInWithUserAsync(UserRole.Admin);

        var users = await client.GetFromJsonAsync<List<UserSummaryDto>>("/api/users");

        var me = Assert.Single(users!, u => u.Id == user.Id);
        Assert.Equal("Admin", me.Role);
        Assert.Single(me.Credentials);
    }

    [Fact]
    public async Task AViewerCannotSeeTheUserList()
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// The footgun this exists for: demoting the only administrator leaves an installation nobody can
    /// administer, and the screen that could fix it is the one they just lost.
    /// </summary>
    [Fact]
    public async Task TheOnlyAdmin_CannotDemoteThemselves()
    {
        var factoryScoped = new AuthenticatedApiFactory();
        try
        {
            var (client, only) = await factoryScoped.SignedInWithUserAsync(UserRole.Admin);

            var response = await client.PutAsJsonAsync($"/api/users/{only.Id}", new { role = "Viewer" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("only enabled administrator", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            factoryScoped.Dispose();
        }
    }

    [Fact]
    public async Task TheOnlyAdmin_CannotDisableThemselves()
    {
        var factoryScoped = new AuthenticatedApiFactory();
        try
        {
            var (client, only) = await factoryScoped.SignedInWithUserAsync(UserRole.Admin);

            var response = await client.PutAsJsonAsync($"/api/users/{only.Id}", new { enabled = false });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            factoryScoped.Dispose();
        }
    }

    /// <summary>
    /// With somebody else to administer the place, demoting is an ordinary decision — and it ends the
    /// demoted user's sessions is not asserted here because a role change is re-read per request
    /// anyway.
    /// </summary>
    [Fact]
    public async Task WithASecondAdmin_DemotingIsAllowed()
    {
        var (client, first) = await factory.SignedInWithUserAsync(UserRole.Admin);
        await factory.SignedInWithUserAsync(UserRole.Admin);

        var response = await client.PutAsJsonAsync($"/api/users/{first.Id}", new { role = "Viewer" });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// An account with no credential cannot be signed into and can only be recovered with an
    /// invitation, which is a support call rather than a decision anybody meant to make.
    /// </summary>
    [Fact]
    public async Task ALastCredential_CannotBeRemoved()
    {
        var (client, user) = await factory.SignedInWithUserAsync(UserRole.Admin);
        var credential = factory.Users.CredentialsOf(user.Id).Single();

        var response = await client.DeleteAsync($"/api/users/{user.Id}/credentials/{credential.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only way this user can sign in", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Enrolling a second one is the recovery story, so removing the first has to work once
    /// there is one.</summary>
    [Fact]
    public async Task WithASecondCredential_TheFirstCanBeRemoved()
    {
        var (client, user) = await factory.SignedInWithUserAsync(UserRole.Admin);
        factory.Users.AddCredential(user.Id, CredentialMethods.Passkey, Guid.NewGuid().ToString("N"), "key", "YubiKey");

        var first = factory.Users.CredentialsOf(user.Id).First();
        var response = await client.DeleteAsync($"/api/users/{user.Id}/credentials/{first.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed record UserCredentialDto(string Id, string Method, string? Label);
    private sealed record UserSummaryDto(
        string Id, string DisplayName, string Role, bool Enabled, List<UserCredentialDto> Credentials);
}
