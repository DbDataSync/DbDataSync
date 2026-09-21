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
        var sessionId = Request.Cookies[AuthOptions.SessionCookie];
        if (string.IsNullOrEmpty(sessionId))
            return Task.FromResult(NetworkFallback());

        // SignalR cannot set headers on a WebSocket handshake, but it does send cookies — which is
        // the other reason this is a cookie scheme rather than a bearer one.
        var user = sessions.Resolve(sessionId, users);
        if (user is null)
            return Task.FromResult(AuthenticateResult.Fail("The session is not valid."));

        return Task.FromResult(AuthenticateResult.Success(Ticket(user)));
    }

    /// <summary>
    /// No session cookie at all — phase 164's replacement for the old blanket <c>Auth:Disabled</c>: a
    /// role-scoped, network-trust fallback rather than one switch that granted Admin to every request
    /// from anywhere. Admin is checked first (the same "both checked, admin wins" precedent
    /// <see cref="WindowsSignIn.Resolve"/> already uses), then Viewer; neither configured means this
    /// falls through to <see cref="AuthenticateResult.NoResult"/>, same as today, requiring a real
    /// sign-in.
    /// </summary>
    private AuthenticateResult NetworkFallback()
    {
        var remote = Context.Connection.RemoteIpAddress;
        // A null remote address is a connection this process cannot attribute — an in-memory test
        // server, or a transport that does not report one — treated as loopback, the same convention
        // RunnerStateGuard's own loopback check already uses and for the same reason: the alternative
        // is that nothing reaches it at all.
        var isLoopback = remote is null || System.Net.IPAddress.IsLoopback(remote);

        if (isLoopback && authOptions.NetworkAdmin == AdminNetworkTrust.Loopback)
            return AuthenticateResult.Success(Anonymous(UserRole.Admin));

        if (authOptions.NetworkViewer == ViewerNetworkTrust.Remote
            || (isLoopback && authOptions.NetworkViewer == ViewerNetworkTrust.Loopback))
        {
            return AuthenticateResult.Success(Anonymous(UserRole.Viewer));
        }

        return AuthenticateResult.NoResult();
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
    /// A network-trust fallback grant: nobody has a name, which is exactly what "trusted by network,
    /// not by sign-in" means. Config commits fall back to the system identity, because attributing them
    /// to a person nobody identified would be a lie.
    /// </summary>
    private static AuthenticationTicket Anonymous(UserRole role)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role.ToString())], SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }
}
