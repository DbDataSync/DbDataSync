using ClrKernel.Core.Secrets;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli;

/// <summary>
/// Thin wrappers over <see cref="SecretStore.Store"/>/<see cref="SecretStore.TryResolve"/>/
/// <see cref="SecretStore.Delete"/> — the same three calls <c>ConfigRepository.SaveConnection</c>
/// already makes for a connection's own credential, reachable here without going through a
/// connection's save flow. This is what "easily settable using the CLI tool" means concretely for
/// phase 79's one standardized ref:
/// <code>dbdatasync secret set dbdatasync:config:stateConnectionString "Password=..."</code>
/// </summary>
public static class SecretCommand
{
    /// <summary>The one ref this build defines a fixed name for (see <see cref="SecretRefs.ForAppSetting"/>)
    /// — what <c>list</c> checks when given no ref of its own to look at.</summary>
    private static readonly string[] KnownRefs = [SecretRefs.ForAppSetting("stateConnectionString")];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var secrets = new SecretStore(true);

        return args[0].ToLowerInvariant() switch
        {
            "set" => Set(args, secrets),
            "list" => List(args, secrets),
            "remove" => Remove(args, secrets),
            var other => Unknown(other),
        };
    }

    private static int Set(string[] args, SecretStore secrets)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: dbdatasync secret set <ref> <value>");
            return 1;
        }

        secrets.Store(args[1], args[2]);
        Console.WriteLine($"Stored '{args[1]}'.");
        return 0;
    }

    /// <summary>
    /// Ref names and whether each currently resolves — never the value itself.
    /// <para>
    /// <see cref="SecretStore"/> has no enumeration API (by design — it's a thin layer over the OS
    /// keyring, which doesn't offer one either), so this cannot show "everything stored" the way a
    /// directory listing would. Given no ref, it checks <see cref="KnownRefs"/> — right now, just the
    /// one fixed ref this phase's starter file documents. Given one or more refs, it checks exactly
    /// those instead, which is how a script or an operator who already knows a connection's ref
    /// (<c>dbdatasync:connection:&lt;name&gt;</c>) checks it without this command having to know
    /// connections exist.
    /// </para>
    /// </summary>
    private static int List(string[] args, SecretStore secrets)
    {
        var refs = args.Length > 1 ? args[1..] : KnownRefs;
        foreach (var reference in refs)
            Console.WriteLine($"{reference}  {(secrets.TryResolve(reference, out _) ? "set" : "not set")}");

        return 0;
    }

    private static int Remove(string[] args, SecretStore secrets)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: dbdatasync secret remove <ref>");
            return 1;
        }

        secrets.Delete(args[1]);
        Console.WriteLine($"Removed '{args[1]}'.");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown secret command '{command}'. Use set, list, or remove.");
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage: dbdatasync secret set <ref> <value>
                   dbdatasync secret list [<ref> ...]
                   dbdatasync secret remove <ref>
            """);
}
