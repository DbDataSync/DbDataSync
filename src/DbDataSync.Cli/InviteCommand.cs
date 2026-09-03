using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Cli;

/// <summary>
/// Prints a fresh invitation.
/// <para>
/// For the case the console has already scrolled, or the process is a service whose output goes
/// nowhere — which on Windows is the normal case, so this is not a convenience.
/// </para>
/// <para>
/// It opens the state database directly rather than calling the API, because the situation it exists
/// for is "nobody can sign in", and an endpoint that needs a session is no help there. (Still counts
/// as "the API's composition root, and one named exception" for <c>StateOwnershipTests</c> — the
/// actual construction happens inside <see cref="StateDatabase.FromOptions"/> now, not here.)
/// </para>
/// </summary>
public static class InviteCommand
{
    public static int Run(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);
        var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";

        // Before phase 79 this constructed a SQLite-backed store unconditionally — no branch for a
        // non-SQLite DbDataSync:StateEngine at all, which meant an admin locked out of an MsSql- or
        // Postgres-backed deployment had no way to recover with this command. Resolved the same way
        // DbDataSyncHost.cs resolves ApiOptions (dbdatasync.config.yaml, then DbDataSync__* environment
        // variables — there is no dedicated --state-engine/--state-connection-string flag; this
        // command's own surface is deliberately small, and both are ordinary DbDataSync:* settings
        // already), then handed to the same factory, so the two never drift on how a non-SQLite state
        // store is reached.
        var config = DbDataSyncConfigFile.Read(root);
        var engine = Enum.TryParse<StateEngine>(
            Environment.GetEnvironmentVariable("DbDataSync__StateEngine") ?? config.GetValueOrDefault("DbDataSync:StateEngine"),
            ignoreCase: true, out var parsedEngine)
            ? parsedEngine
            : StateEngine.Sqlite;
        var stateConnectionString =
            Environment.GetEnvironmentVariable("DbDataSync__StateConnectionString")
            ?? config.GetValueOrDefault("DbDataSync:StateConnectionString");

        if (engine == StateEngine.Sqlite && !File.Exists(stateDb))
        {
            Console.Error.WriteLine(
                $"No state database at '{stateDb}'. Start DbDataSync once before inviting anybody, or " +
                "pass --state-db.");
            return 1;
        }

        var role = CliOptions.Read(args, "--role") ?? nameof(UserRole.Admin);
        if (!Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed))
        {
            Console.Error.WriteLine($"'{role}' is not a role. Use Admin or Viewer.");
            return 1;
        }

        StateDatabase database;
        try
        {
            database = StateDatabase.FromOptions(engine, stateDb, stateConnectionString, new SecretStore(true));
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var minted = new InviteStore(database).Create(parsed, forUserId: null, createdByUserId: null);

        Console.WriteLine($"An invitation for a {parsed}, valid until {minted.Invite.ExpiresAtUtc:u}:");
        Console.WriteLine();
        Console.WriteLine($"    {url.TrimEnd('/')}/invite#{minted.Code}");
        Console.WriteLine();
        Console.WriteLine("It can be used once, and this is the only time it is shown.");
        return 0;
    }
}
