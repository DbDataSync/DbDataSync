using DbDataSync.Api.Configuration;
using DbDataSync.State;
using Microsoft.Extensions.Hosting;

namespace DbDataSync.Api.Auth;

/// <summary>
/// The only way into a fresh install, printed on the console and written to
/// <c>&lt;RepoRoot&gt;/FIRST-RUN.txt</c>.
/// <para>
/// This is what lets a freshly installed tool be **closed by default** rather than open. Without it
/// the choice is between starting open — which is how products get breached — and refusing to start,
/// which makes a tool somebody just installed unusable.
/// </para>
/// <para>
/// Reprinted (and rewritten) on every start while there are still no users, because the first line of
/// a service's log is not somewhere anybody looks twice — a service has no console at all — and
/// removed the moment the first user exists, so it does not sit on disk as a standing way in.
/// </para>
/// </summary>
public sealed class BootstrapInvite(
    UserStore users, InviteStore invites, AuthOptions authOptions, ApiOptions apiOptions,
    ILogger<BootstrapInvite> logger)
    : IHostedService
{
    public const string FileName = "FIRST-RUN.txt";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (authOptions.Disabled)
            return Task.CompletedTask;

        var filePath = Path.Combine(apiOptions.RepoRoot, FileName);

        if (users.Any())
        {
            // Somebody exists, so an unredeemed bootstrap invite is a way in that nobody needs and
            // everybody with the console history — or this file — has.
            invites.DeleteBootstrapInvites();
            TryDelete(filePath);
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

        // Best effort: a service running as an account with no write access to RepoRoot still has the
        // log line above, so this never blocks startup over a permissions problem it can't fix anyway.
        try
        {
            File.WriteAllText(
                filePath,
                $"No users exist yet. Open this once to create the first administrator — it expires in " +
                $"{(int)InviteStore.DefaultLifetime.TotalHours} hours:\n\n" +
                $"    /invite#{minted.Code}\n\n" +
                "Run `dbdatasync invite` to print a fresh one if this expires. This file is removed once " +
                "the first administrator signs up.\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {FileName}.", FileName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, same reasoning as the write above.
        }
    }
}
