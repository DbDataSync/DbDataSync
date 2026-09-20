using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config get|set &lt;key&gt; [value]</c> — the CLI door onto the same settings the Admin Configuration screen
/// and <c>setup</c> edit (phase 161). It exists so a setting reachable from the web console is reachable from a script too,
/// rather than only by hand-editing <c>dbdatasync.config.yaml</c>.
/// <para>
/// It knows no keys of its own: what may be set, what it defaults to, and what to warn about all come from the Admin
/// screen's catalog (<see cref="AdminConfigService.Writable"/>), so a key added there is settable here with no second edit,
/// and one the screen refuses (a nested key, a secret) is refused here for the same reason. It writes through the same
/// <see cref="DbDataSyncConfigFile"/> and makes the same one-line commit the screen does, so config history reads the same
/// whichever door was used.
/// </para>
/// </summary>
public static class ConfigValueCommand
{
    public static int Set(string[] args)
    {
        var positional = Positional(args);
        if (positional.Count != 2)
            return Usage("config set <key> <value> [--repo <path>]");

        if (Resolve(positional[0]) is not { } key)
            return UnknownKey(positional[0]);

        var value = positional[1];
        if (key.DefaultValue is "true" or "false")
        {
            if (!bool.TryParse(value, out var flag))
            {
                Console.Error.WriteLine($"{key.Key} is on or off — give true or false, not '{value}'.");
                return 1;
            }
            value = flag ? "true" : "false";
        }

        var root = DbDataSyncRoot.Resolve(args);
        // A git repository at the root, which `serve` and `setup` create along with the starter file — not ExistingSetup, which
        // also requires the file to already hold a value, and a fresh starter is only comments.
        if (!LibGit2Sharp.Repository.IsValid(root))
        {
            Console.Error.WriteLine($"No DbDataSync configuration at {root}. Run `dbdatasync setup` (or `dbdatasync serve`) first, or pass --repo <path>.");
            return 1;
        }

        // Shown before the write, whichever surface changed it — an operator scripting this should read it too. Only when the
        // value moves away from the default: turning a risky setting back off needs no warning.
        if (key.Caution is not null && !string.Equals(value, key.DefaultValue, StringComparison.Ordinal))
        {
            Console.WriteLine("Warning:");
            Console.WriteLine($"  {key.Caution}");
        }

        DbDataSyncConfigFile.SetValue(root, "DbDataSync", LocalName(key), value);
        new GitCommitService(root).CommitChanges(
            [DbDataSyncConfigFile.PathIn(root)], $"Set '{key.Key}' in dbdatasync.config.yaml", CurrentUser.SystemAuthor);

        Console.WriteLine($"Set {key.Key} to {value} in {DbDataSyncConfigFile.PathIn(root)}. Restart the service for it to take effect.");
        return 0;
    }

    public static int Get(string[] args)
    {
        var positional = Positional(args);
        if (positional.Count != 1)
            return Usage("config get <key> [--repo <path>]");

        if (Resolve(positional[0]) is not { } key)
            return UnknownKey(positional[0]);

        var root = DbDataSyncRoot.Resolve(args);
        // The file only: an environment variable or a command-line flag can override it in a running service, and this command
        // cannot see those — the Admin screen's "Running" column is where the two are compared.
        var inFile = DbDataSyncConfigFile.Read(root).TryGetValue(key.Key, out var value) && value is not null;

        Console.WriteLine(inFile
            ? $"{key.Key} = {value}"
            : $"{key.Key} is not set in {DbDataSyncConfigFile.PathIn(root)}" + (key.DefaultValue is null ? "." : $" (default: {key.DefaultValue})."));
        return 0;
    }

    private static WritableConfigKey? Resolve(string name) => AdminConfigService.Writable(name);

    private static string LocalName(WritableConfigKey key) => key.Key["DbDataSync:".Length..];

    /// <summary>The arguments that are not options: <c>--repo</c> and its value are set aside, everything else is in order.</summary>
    private static List<string> Positional(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo") { i++; continue; }
            if (args[i].StartsWith("--repo=", StringComparison.Ordinal)) continue;
            positional.Add(args[i]);
        }
        return positional;
    }

    private static int UnknownKey(string name)
    {
        Console.Error.WriteLine($"'{name}' is not a setting this command can change. Settable: {string.Join(", ", AdminConfigService.WritableKeyNames())}.");
        return 1;
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"Usage: dbdatasync {usage}");
        return 1;
    }
}
