using DbDataSync.Api.Auth;
using DbDataSync.State;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

public sealed record UserCredentialSummary(
    string Id, string Method, string? Label, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc);

public sealed record UserSummary(
    string Id, string DisplayName, string? Email, string Role, bool Enabled,
    IReadOnlyList<UserCredentialSummary> Credentials);

public sealed record UpdateUserRequest(string? Role, bool? Enabled);

/// <summary>
/// Who exists, what they may do, and how they get in. Admin-only by the fallback policy.
/// </summary>
[ApiController]
[Route("api/users")]
public sealed class UsersController(UserStore users, SessionStore sessions, CurrentUser currentUser)
    : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<UserSummary>> List() =>
        Ok(users.List().Select(Summarise).ToList());

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateUserRequest request)
    {
        if (users.Get(id) is not { } user)
            return NotFound();

        if (request.Role is { } roleName)
        {
            if (!Enum.TryParse<UserRole>(roleName, ignoreCase: true, out var role))
                return BadRequest(new { error = $"'{roleName}' is not a role. Use Admin or Viewer." });

            if (Demoting(user, role) is { } problem)
                return BadRequest(new { error = problem });

            users.SetRole(id, role);
        }

        if (request.Enabled is { } enabled)
        {
            if (!enabled && LastAdmin(user))
            {
                return BadRequest(new
                {
                    error =
                        "This is the only enabled administrator. Disabling them would lock everybody out " +
                        "of the tool that manages the lockout — promote somebody else first.",
                });
            }

            users.SetEnabled(id, enabled);
            if (!enabled)
                sessions.DeleteAllFor(id);
        }

        return Ok(Summarise(users.Get(id)!));
    }

    /// <summary>
    /// Removes one way in. Refused when it is the person's last, because an account with no credential
    /// cannot be signed into and cannot be given a new one except by an invite — which is a support
    /// call rather than a decision anybody meant to make.
    /// </summary>
    [HttpDelete("{id}/credentials/{credentialId}")]
    public IActionResult RemoveCredential(string id, string credentialId)
    {
        if (users.Get(id) is null)
            return NotFound();

        if (users.CredentialsOf(id).Count <= 1)
        {
            return BadRequest(new
            {
                error =
                    "This is the only way this user can sign in. Enrol another credential before " +
                    "removing this one — an account with none can only be recovered with an invitation.",
            });
        }

        return users.RemoveCredential(id, credentialId) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Locking every administrator out of the tool that manages the lockout is a real way to lose an
    /// afternoon, and the check is three lines.
    /// </summary>
    private string? Demoting(UserRecord user, UserRole to)
    {
        if (to == UserRole.Admin || user.Role != UserRole.Admin)
            return null;

        return LastAdmin(user)
            ? "This is the only enabled administrator. Promote somebody else before demoting them."
            : null;
    }

    private bool LastAdmin(UserRecord user) =>
        user.Role == UserRole.Admin
        && users.List().Count(u => u.Role == UserRole.Admin && u.Enabled && u.Id != user.Id) == 0;

    private UserSummary Summarise(UserRecord user) => new(
        user.Id, user.DisplayName, user.Email, user.Role.ToString(), user.Enabled,
        users.CredentialsOf(user.Id)
            .Select(c => new UserCredentialSummary(c.Id, c.Method, c.Label, c.CreatedAtUtc, c.LastUsedAtUtc))
            .ToList());
}
