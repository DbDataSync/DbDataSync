using System.Security.Claims;
using System.Text.Encodings.Web;
using DbDataSync.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DbDataSync.Api.Auth;

/// <summary>
/// Turns the session cookie into a principal.
/// <para>
/// A cookie rather than a JWT: there is no second service to present a token to, and a cookie backed
/// by a row is revocable — signing somebody out or disabling them takes effect on their next request
/// rather than whenever a token would have expired. The row is re-read every request for exactly that
/// reason.
/// </para>
/// <para>
/// This is the *only* authentication scheme the app uses. Windows negotiation happens on one
/// endpoint, which mints a session; every other request — including SignalR's — is a session cookie.
/// That keeps one answer to "who is this" rather than one per method.
/// </para>
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionStore sessions,
    UserStore users,
    AuthOptions authOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DbDataSyncSession";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (authOptions.Disabled)
            return Task.FromResult(AuthenticateResult.Success(Anonymous()));

        var sessionId = Request.Cookies[AuthOptions.SessionCookie];
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult(AuthenticateResult.NoResult());

        // SignalR cannot set headers on a WebSocket handshake, but it does send cookies — which is
        // the other reason this is a cookie scheme rather than a bearer one.
        var user = sessions.Resolve(sessionId, users);
        if (user is null)
            return Task.FromResult(AuthenticateResult.Fail("The session is not valid."));

        return Task.FromResult(AuthenticateResult.Success(Ticket(user)));
    }

    private AuthenticationTicket Ticket(UserRecord user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(CurrentUser.UserIdClaim, user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(ClaimTypes.Email, user.Email));

        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
    }

    /// <summary>
    /// The authentication-disabled deployment: everybody is an admin and nobody has a name, which is
    /// exactly what "we chose not to authenticate" means. Config commits fall back to the system
    /// identity, because attributing them to a person nobody identified would be a lie.
    /// </summary>
    private static AuthenticationTicket Anonymous()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, nameof(UserRole.Admin))], SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }
}
