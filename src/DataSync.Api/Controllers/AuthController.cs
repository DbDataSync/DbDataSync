using DataSync.Api.Auth;
using DataSync.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <param name="Methods">Which sign-in methods this deployment offers, so the sign-in screen shows the
/// ones that exist rather than every one that was ever built.</param>
public sealed record AuthStatus(
    bool Authenticated, string? UserId, string? DisplayName, string? Role, IReadOnlyList<string> Methods);

/// <summary>
/// Signing in, signing out, and asking who you are.
/// <para>
/// Anonymous by necessity: a sign-in endpoint that required a session would be a door locked from the
/// inside. Everything else in the app is closed by default.
/// </para>
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(
    AuthOptions options, UserStore users, SessionStore sessions, CurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Who the caller is, and how they could sign in. The one endpoint the SPA can always call — the
    /// answer to "am I signed in" cannot itself require being signed in.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<AuthStatus> Status()
    {
        var methods = new List<string>();
        if (options.WindowsEnabled)
            methods.Add("windows");

        return Ok(new AuthStatus(
            currentUser.Id is not null || options.Disabled,
            currentUser.Id,
            currentUser.Name,
            currentUser.Role?.ToString(),
            methods));
    }

    /// <summary>
    /// Negotiates a Windows identity and, if it is in one of the configured groups, mints a session.
    /// <para>
    /// A dedicated endpoint rather than negotiating on every request: Kerberos on every call is a cost
    /// for no benefit once a session exists, and it keeps one answer to "who is this" — the session
    /// cookie — for the controllers and for SignalR alike.
    /// </para>
    /// </summary>
    [HttpPost("windows")]
    [Authorize(AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme)]
    public IActionResult SignInWithWindows()
    {
        if (!options.WindowsEnabled)
            return BadRequest(new { error = "Windows authentication is not configured on this deployment." });

        var identity = WindowsSignIn.Resolve(HttpContext.User, options);
        if (identity is null)
        {
            return BadRequest(new
            {
                error = "This deployment is not running on Windows, so it cannot authenticate a Windows identity.",
            });
        }

        if (identity.Role is not { } role)
        {
            // Not a 403 about a missing permission: this account is not a user of this application at
            // all, and saying which groups would grant access is the actionable half.
            return StatusCode(403, new
            {
                error =
                    $"'{identity.Name}' is not a member of a group this deployment grants access to. " +
                    $"Admin group: {options.AdminGroup ?? "(none)"}; viewer group: {options.ViewerGroup ?? "(none)"}.",
            });
        }

        var user = WindowsSignIn.Upsert(users, identity, role);
        if (!user.Enabled)
            return StatusCode(403, new { error = $"'{user.DisplayName}' is disabled in DataSync." });

        IssueSession(user);
        return Ok(new AuthStatus(true, user.Id, user.DisplayName, user.Role.ToString(), ["windows"]));
    }

    [HttpPost("sign-out")]
    public IActionResult SignOutOfDataSync()
    {
        if (Request.Cookies[AuthOptions.SessionCookie] is { } sessionId)
            sessions.Delete(sessionId);

        Response.Cookies.Delete(AuthOptions.SessionCookie);
        return NoContent();
    }

    private void IssueSession(UserRecord user)
    {
        var session = sessions.Create(user.Id);

        Response.Cookies.Append(AuthOptions.SessionCookie, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            // Only over HTTPS when the request itself was — a `Secure` cookie on a plain-HTTP
            // deployment is a cookie the browser never sends back, which reads as "sign-in silently
            // does nothing".
            Secure = Request.IsHttps,
            Expires = session.ExpiresAtUtc,
            Path = "/",
        });
    }
}
