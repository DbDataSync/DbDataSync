using System.Security.Claims;
using DbDataSync.Core.Git;
using DbDataSync.State;

namespace DbDataSync.Api.Auth;

/// <summary>
/// Who is making this request, resolved per request from the signed-in principal.
/// <para>
/// It exists mostly for <see cref="Author"/>. Every config write used to be committed as a fixed
/// <c>GitAuthor("DbDataSync API", …)</c>, with a comment saying so until per-user auth existed — so the
/// config history the Version Control tab shows could say what changed and never who. This is what
/// makes that history true, and is the reason this feature is worth more than access control.
/// </para>
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, AuthOptions options)
{
    public const string UserIdClaim = "dbdatasync:userId";

    public string? Id => accessor.HttpContext?.User.FindFirst(UserIdClaim)?.Value;

    public string? Name => accessor.HttpContext?.User.Identity?.Name;

    public UserRole? Role =>
        accessor.HttpContext?.User.FindFirst(ClaimTypes.Role)?.Value is { } role
        && Enum.TryParse<UserRole>(role, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Who a config commit is attributed to.
    /// <para>
    /// Falls back to the old fixed identity only where there is genuinely nobody — an unauthenticated
    /// deployment, or a background service writing config on its own. A commit attributed to a person
    /// who was not there would be worse than one attributed to the system.
    /// </para>
    /// </summary>
    public GitAuthor Author
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            var name = principal?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(name))
                return SystemAuthor;

            var email = principal?.FindFirst(ClaimTypes.Email)?.Value;
            return new GitAuthor(name, string.IsNullOrWhiteSpace(email) ? "dbdatasync@localhost" : email);
        }
    }

    /// <summary>What a write is attributed to when nobody is signed in — an authentication-disabled
    /// deployment, or the scheduler acting on its own.</summary>
    public static GitAuthor SystemAuthor { get; } = new("DbDataSync", "dbdatasync@localhost");

    /// <summary>Whether this deployment authenticates at all, for a UI that has to decide whether to
    /// show a sign-in state or nothing.</summary>
    public bool AuthenticationConfigured => !options.Disabled;
}
