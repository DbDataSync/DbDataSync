using DbDataSync.Api.Auth;
using DbDataSync.State;
using Fido2NetLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

public sealed record CreateInviteRequest(string Role, string? ForUserId);

/// <param name="Url">Shown once, and never recoverable — the code is hashed the moment it is made.</param>
public sealed record CreatedInvite(string Url, DateTimeOffset ExpiresAtUtc, string Role);

public sealed record InviteSummary(
    string Id, string Role, string? UserId, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <param name="DisplayName">Ignored when the invite names an existing user — that person already has
/// a name, and letting an invite rename them would be a rename disguised as an enrolment.</param>
public sealed record RedeemInviteRequest(string Code, string DisplayName, string? Email);

/// <summary>
/// Invitations: making them, and redeeming one by enrolling a passkey.
/// <para>
/// Redemption is anonymous by necessity — the whole point is that the person redeeming has no way in
/// yet. What protects it is the code, which is a 256-bit random value that exists in exactly one
/// place: whatever the inviter pasted into a message.
/// </para>
/// </summary>
[ApiController]
[Route("api/invites")]
public sealed class InvitesController(
    InviteStore invites,
    UserStore users,
    SessionStore sessions,
    PasskeyService passkeys,
    CurrentUser currentUser,
    AuthOptions authOptions) : ControllerBase
{
    private const string RegistrationStateCookie = "dbdatasync.passkey-registration";
    private const string AssertionStateCookie = "dbdatasync.passkey-assertion";

    [HttpPost]
    public ActionResult<CreatedInvite> Create([FromBody] CreateInviteRequest request)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            return BadRequest(new { error = $"'{request.Role}' is not a role. Use Admin or Viewer." });

        if (request.ForUserId is not null && users.Get(request.ForUserId) is null)
            return NotFound(new { error = "No such user." });

        var minted = invites.Create(role, request.ForUserId, currentUser.Id);
        return Ok(new CreatedInvite(UrlFor(minted.Code), minted.Invite.ExpiresAtUtc, role.ToString()));
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<InviteSummary>> List() =>
        Ok(invites.ListOutstanding()
            .Select(i => new InviteSummary(i.Id, i.Role.ToString(), i.UserId, i.CreatedAtUtc, i.ExpiresAtUtc))
            .ToList());

    /// <summary>Whether a code is worth showing a redemption screen for. Deliberately says only yes or
    /// no — "expired" and "already used" and "never existed" are one answer to somebody guessing.</summary>
    [AllowAnonymous]
    [HttpGet("check")]
    public IActionResult Check([FromQuery] string code) =>
        invites.Find(code) is null
            ? NotFound(new { error = "This invitation is not valid. It may have been used already, or expired." })
            : Ok(new { valid = true });

    /// <summary>Starts a passkey enrolment against an invite.</summary>
    [AllowAnonymous]
    [HttpPost("begin-registration")]
    public IActionResult BeginRegistration([FromBody] RedeemInviteRequest request)
    {
        if (invites.Find(request.Code) is not { } invite)
            return NotFound(new { error = "This invitation is not valid." });

        var displayName = invite.UserId is { } existing
            ? users.Get(existing)?.DisplayName ?? request.DisplayName
            : request.DisplayName;

        if (string.IsNullOrWhiteSpace(displayName))
            return BadRequest(new { error = "A name is required." });

        var (options, state) = passkeys.BeginRegistration(invite.UserId ?? Guid.NewGuid().ToString("N"), displayName);

        // Held in a short-lived cookie rather than round-tripped through the client: a challenge the
        // caller supplies is a challenge they can choose, and then the response they send answers a
        // question they asked themselves.
        StoreState(RegistrationStateCookie, state);
        return Ok(options);
    }

    [AllowAnonymous]
    [HttpPost("complete-registration")]
    public async Task<IActionResult> CompleteRegistration(
        [FromQuery] string code,
        [FromQuery] string displayName,
        [FromQuery] string? email,
        [FromBody] AuthenticatorAttestationRawResponse response,
        CancellationToken cancellationToken)
    {
        if (Request.Cookies[RegistrationStateCookie] is not { } state)
            return BadRequest(new { error = "This registration was not started here, or has expired. Try again." });

        if (invites.Find(code) is not { } invite)
            return NotFound(new { error = "This invitation is not valid." });

        string credentialId;
        string publicKey;
        try
        {
            (credentialId, publicKey) = await passkeys.CompleteRegistrationAsync(state, response, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            return BadRequest(new { error = $"The passkey could not be verified: {ex.Message}" });
        }

        var user = invite.UserId is { } existingId
            ? users.Get(existingId)!
            : users.CreateUser(displayName, email, invite.Role);

        // Redeemed *after* the user exists and before the credential is added, so a race that loses
        // here leaves an account with no passkey rather than an invitation somebody can use twice.
        if (!invites.Redeem(invite.Id, user.Id))
            return Conflict(new { error = "This invitation has already been used." });

        users.AddCredential(user.Id, CredentialMethods.Passkey, credentialId, publicKey, label: "Passkey");

        // The bootstrap invite exists only while there is no way in. There is now.
        invites.DeleteBootstrapInvites();

        Response.Cookies.Delete(RegistrationStateCookie);
        IssueSession(user);

        return Ok(new AuthStatus(true, user.Id, user.DisplayName, user.Role.ToString(), ["passkey"]));
    }

    [AllowAnonymous]
    [HttpPost("~/api/auth/passkey/begin")]
    public IActionResult BeginAssertion()
    {
        var (options, state) = passkeys.BeginAssertion();
        StoreState(AssertionStateCookie, state);
        return Ok(options);
    }

    [AllowAnonymous]
    [HttpPost("~/api/auth/passkey/complete")]
    public async Task<IActionResult> CompleteAssertion(
        [FromBody] AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken)
    {
        if (Request.Cookies[AssertionStateCookie] is not { } state)
            return BadRequest(new { error = "This sign-in was not started here, or has expired. Try again." });

        var user = await passkeys.CompleteAssertionAsync(state, response, cancellationToken);
        if (user is null)
            return Unauthorized(new { error = "That passkey is not registered here, or did not verify." });

        Response.Cookies.Delete(AssertionStateCookie);
        IssueSession(user);

        return Ok(new AuthStatus(true, user.Id, user.DisplayName, user.Role.ToString(), ["passkey"]));
    }

    private void StoreState(string name, string state) =>
        Response.Cookies.Append(name, state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddMinutes(5),
            Path = "/",
        });

    private void IssueSession(UserRecord user)
    {
        var session = sessions.Create(user.Id);
        Response.Cookies.Append(AuthOptions.SessionCookie, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Expires = session.ExpiresAtUtc,
            Path = "/",
        });
    }

    /// <summary>
    /// The code goes in the **fragment**, which browsers do not send to a server and access logs
    /// therefore never record. It is a bearer credential; putting it in the path would write it into
    /// every proxy log between the inviter and the invitee.
    /// </summary>
    private string UrlFor(string code) =>
        $"{Request.Scheme}://{Request.Host}/invite#{code}";
}
