using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// Invitations: the one-time capability that makes a fresh install closed by default rather than open.
/// <para>
/// The passkey ceremony itself is not exercised here — that needs a real WebAuthn authenticator, and
/// mocking one would test the mock. What is exercised is everything around it, which is where the
/// security properties live: single use, expiry, hashing, and the bootstrap's lifecycle.
/// </para>
/// </summary>
public sealed class InviteTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private InviteStore Invites => factory.Services.GetRequiredService<InviteStore>();

    [Fact]
    public async Task AnAdminCanMintAnInvite_AndItComesBackAsAUrlOnce()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/invites", new { role = "Viewer" });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var url = created.GetProperty("url").GetString()!;
        // The fragment, not the path: browsers do not send it to a server, so it stays out of every
        // access log between the inviter and the invitee.
        Assert.Contains("/invite#", url);
        Assert.Equal("Viewer", created.GetProperty("role").GetString());
    }

    /// <summary>A viewer minting invitations would be a viewer creating administrators.</summary>
    [Fact]
    public async Task AViewerCannotMintAnInvite()
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.PostAsJsonAsync("/api/invites", new { role = "Admin" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The code exists in exactly one place — whatever the inviter pasted into a message.
    /// A database somebody can read must not be a database somebody can sign in from.</summary>
    [Fact]
    public void TheCodeIsNeverStored()
    {
        var minted = Invites.Create(UserRole.Viewer);

        var database = factory.Services.GetRequiredService<StateDatabase>();
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CodeHash FROM Invites WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", minted.Invite.Id);

        var stored = (string)cmd.ExecuteScalar()!;
        Assert.NotEqual(minted.Code, stored);
        Assert.DoesNotContain(minted.Code, stored);
    }

    [Fact]
    public void AnInviteIsFoundByItsCode_AndOnlyByIt()
    {
        var minted = Invites.Create(UserRole.Admin);

        Assert.NotNull(Invites.Find(minted.Code));
        Assert.Null(Invites.Find(minted.Code + "x"));
    }

    /// <summary>
    /// The `RedeemedAtUtc IS NULL` in the update is what makes single use true rather than intended:
    /// two redemptions racing both find the invite valid, and only one updates a row.
    /// </summary>
    [Fact]
    public void AnInviteRedeemsOnce()
    {
        var minted = Invites.Create(UserRole.Viewer);

        Assert.True(Invites.Redeem(minted.Invite.Id, "user-a"));
        Assert.False(Invites.Redeem(minted.Invite.Id, "user-b"));
        Assert.Null(Invites.Find(minted.Code));
    }

    [Fact]
    public void AnExpiredInvite_IsNotFound()
    {
        var minted = Invites.Create(UserRole.Viewer, lifetime: TimeSpan.FromMilliseconds(-1));

        Assert.Null(Invites.Find(minted.Code));
    }

    /// <summary>Checking says only yes or no. "Expired", "already used" and "never existed" are one
    /// answer to somebody working through guesses.</summary>
    [Fact]
    public async Task CheckingAnUnusableCode_SaysNothingAboutWhy()
    {
        var minted = Invites.Create(UserRole.Viewer);
        Invites.Redeem(minted.Invite.Id, "somebody");

        var client = factory.CreateClient();
        var used = await client.GetAsync($"/api/invites/check?code={Uri.EscapeDataString(minted.Code)}");
        var never = await client.GetAsync("/api/invites/check?code=not-a-real-code");

        Assert.Equal(HttpStatusCode.NotFound, used.StatusCode);
        Assert.Equal(await never.Content.ReadAsStringAsync(), await used.Content.ReadAsStringAsync());
    }

    /// <summary>Redemption has to be reachable by somebody with no way in — that is the entire
    /// point.</summary>
    [Fact]
    public async Task RedemptionIsReachableWithoutASession()
    {
        var minted = Invites.Create(UserRole.Viewer);

        var response = await factory.CreateClient()
            .GetAsync($"/api/invites/check?code={Uri.EscapeDataString(minted.Code)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
