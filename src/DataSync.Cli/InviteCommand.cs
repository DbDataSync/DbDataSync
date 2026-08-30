using DataSync.State;

namespace DataSync.Cli;

/// <summary>
/// Prints a fresh invitation.
/// <para>
/// For the case the console has already scrolled, or the process is a service whose output goes
/// nowhere — which on Windows is the normal case, so this is not a convenience.
/// </para>
/// <para>
/// It opens the state database directly rather than calling the API, because the situation it exists
/// for is "nobody can sign in", and an endpoint that needs a session is no help there.
/// </para>
/// </summary>
public static class InviteCommand
{
    public static int Run(string[] args)
    {
        var root = CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot;
        var stateDb = CliOptions.Read(args, "--state-db") ?? Path.Combine(root, "state.db");
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";

        if (!File.Exists(stateDb))
        {
            Console.Error.WriteLine(
                $"No state database at '{stateDb}'. Start DataSync once before inviting anybody, or " +
                "pass --state-db.");
            return 1;
        }

        var role = CliOptions.Read(args, "--role") ?? nameof(UserRole.Admin);
        if (!Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed))
        {
            Console.Error.WriteLine($"'{role}' is not a role. Use Admin or Viewer.");
            return 1;
        }

        var database = new StateDatabase(stateDb);
        var minted = new InviteStore(database).Create(parsed, forUserId: null, createdByUserId: null);

        Console.WriteLine($"An invitation for a {parsed}, valid until {minted.Invite.ExpiresAtUtc:u}:");
        Console.WriteLine();
        Console.WriteLine($"    {url.TrimEnd('/')}/invite#{minted.Code}");
        Console.WriteLine();
        Console.WriteLine("It can be used once, and this is the only time it is shown.");
        return 0;
    }
}
