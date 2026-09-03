using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using DbDataSync.State;

namespace DbDataSync.Api.Auth;

/// <param name="Role">Null when this identity is in neither configured group — which is "not a user
/// here", not "a user with no permissions".</param>
public sealed record WindowsIdentityResult(string Sid, string Name, UserRole? Role);

/// <summary>
/// Turning a negotiated Windows identity into one of this application's users.
/// <para>
/// **Group membership is evaluated per sign-in and again on every request**, never baked into a
/// session at sign-in: removing somebody from the admin group has to remove their access without
/// waiting for a cookie to expire. The session carries the user id; the role comes from the user row,
/// which the Windows path keeps in step.
/// </para>
/// <para>
/// The **SID** is the subject, not the account name. Names get renamed and reused; a SID does not, and
/// keying on a name is how a new employee inherits a former one's access.
/// </para>
/// </summary>
public static class WindowsSignIn
{
    /// <summary>
    /// What this Windows principal is entitled to, or null when it is not one of ours.
    /// <para>
    /// Returns null off Windows too, rather than throwing: a Linux deployment simply does not have
    /// this method available, and the endpoint that calls it says so.
    /// </para>
    /// </summary>
    public static WindowsIdentityResult? Resolve(ClaimsPrincipal principal, AuthOptions options)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        return ResolveOnWindows(principal, options);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsIdentityResult? ResolveOnWindows(ClaimsPrincipal principal, AuthOptions options)
    {
        if (principal.Identity is not WindowsIdentity { IsAuthenticated: true } identity)
            return null;

        var windowsPrincipal = new WindowsPrincipal(identity);
        var sid = identity.User?.Value;
        if (sid is null)
            return null;

        // Both checked, and admin wins. Somebody in both groups was put in the admin group by
        // somebody who meant it.
        var isAdmin = !string.IsNullOrWhiteSpace(options.AdminGroup) && windowsPrincipal.IsInRole(options.AdminGroup);
        var isViewer = !string.IsNullOrWhiteSpace(options.ViewerGroup) && windowsPrincipal.IsInRole(options.ViewerGroup);

        var role = isAdmin ? UserRole.Admin : isViewer ? UserRole.Viewer : (UserRole?)null;
        return new WindowsIdentityResult(sid, identity.Name ?? sid, role);
    }

    /// <summary>
    /// The user this SID maps to, created on first sign-in and kept in step with the directory after.
    /// <para>
    /// The role is written back on every sign-in because the group is the authority — a user moved
    /// from the viewer group to the admin group should be an admin the next time they sign in, without
    /// anybody editing them here.
    /// </para>
    /// </summary>
    public static UserRecord Upsert(UserStore users, WindowsIdentityResult identity, UserRole role)
    {
        var existing = users.FindByCredential(CredentialMethods.Windows, identity.Sid);
        if (existing is null)
        {
            var created = users.CreateUser(identity.Name, email: null, role);
            users.AddCredential(created.Id, CredentialMethods.Windows, identity.Sid, label: identity.Name);
            return created;
        }

        if (existing.Role != role)
        {
            users.SetRole(existing.Id, role);
            return existing with { Role = role };
        }

        return existing;
    }
}
