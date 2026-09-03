using DbDataSync.State;
using Microsoft.Extensions.Hosting;

namespace DbDataSync.Api.Auth;

/// <summary>
/// The only way into a fresh install, printed on the console.
/// <para>
/// This is what lets a freshly installed tool be **closed by default** rather than open. Without it
/// the choice is between starting open — which is how products get breached — and refusing to start,
/// which makes a tool somebody just installed unusable.
/// </para>
/// <para>
/// Reprinted on every start while there are still no users, because the first line of a service's log
/// is not somewhere anybody looks twice, and revoked the moment the first user exists.
/// </para>
/// </summary>
public sealed class BootstrapInvite(
    UserStore users, InviteStore invites, AuthOptions authOptions, ILogger<BootstrapInvite> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (authOptions.Disabled)
            return Task.CompletedTask;

        if (users.Any())
        {
            // Somebody exists, so an unredeemed bootstrap invite is a way in that nobody needs and
            // everybody with the console history has.
            invites.DeleteBootstrapInvites();
            return Task.CompletedTask;
        }

        // Replaced rather than reused: a code printed to a console that has since scrolled away is a
        // code nobody has, and one that outlives several restarts is one that has been in more places
        // than it should.
        invites.DeleteBootstrapInvites();
        var minted = invites.Create(UserRole.Admin, forUserId: null, createdByUserId: null);

        logger.LogWarning(
            "No users exist yet. Open this once to create the first administrator — it expires in {Hours} hours:\n" +
            "    /invite#{Code}\n" +
            "Run `dbdatasync invite` to print a fresh one if this scrolls away.",
            (int)InviteStore.DefaultLifetime.TotalHours,
            minted.Code);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
