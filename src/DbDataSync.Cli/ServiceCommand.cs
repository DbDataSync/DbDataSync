using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DbDataSync.Cli;

/// <summary>
/// Registers this tool as a Windows service, and says what it did.
/// <para>
/// The binary registered is **the tool's own apphost shim** — the <c>dbdatasync.exe</c> the SDK put in
/// the user's tools directory — with <c>serve</c> and fully resolved paths as its arguments. Resolved
/// at install time and printed, because a service has no console to say "I could not find my config
/// repository" on: the moment to find that out is now.
/// </para>
/// </summary>
public static class ServiceCommand
{
    public const string ServiceName = "DbDataSync";

    public static int Run(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine(
                "Service registration is Windows-only. On Linux, run `dbdatasync serve` under systemd, " +
                "or use the container image.");
            return 1;
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync service install|uninstall|status");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "install" => Install(args),
            "uninstall" => Sc("delete", ServiceName),
            "status" => Sc("query", ServiceName),
            var other => Unknown(other),
        };
    }

    private static int Install(string[] args)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this tool's own executable path.");
            return 1;
        }

        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";
        var account = CliOptions.Read(args, "--account");

        // Quoted as one binPath value, which is what sc.exe takes — and the space after `binPath=` is
        // load-bearing in sc.exe's own argument syntax, which is a thing it will not tell you.
        var binPath = $"\"{executable}\" serve --repo \"{root}\" --url {url}";

        var arguments = new List<string>
        {
            "create", ServiceName, $"binPath= {binPath}", "start= auto", "DisplayName= DbDataSync",
        };

        if (account is not null)
            arguments.Add($"obj= {account}");

        Console.WriteLine($"Registering the '{ServiceName}' service:");
        Console.WriteLine($"  executable         {executable}");
        Console.WriteLine($"  config repository  {Path.Combine(root, "config")}");
        Console.WriteLine($"  console            {url}");
        Console.WriteLine($"  account            {account ?? "LocalSystem"}");

        // Not cosmetic: a connection using IntegratedAuth connects *as the service account*, and
        // phase 52's Windows auth authorises against a group this account may or may not be in. The
        // install command is where an operator finds that out rather than at the first failed run.
        Console.WriteLine();
        Console.WriteLine(
            "  Connections using integrated authentication will connect as this account.");

        GrantDataDirectoryAccess(root, account);

        return Sc([.. arguments]);
    }

    /// <summary>
    /// Issuing and granting are one operation from the operator's point of view — the same principle
    /// phase 82 applied to a certificate's private key. Phase 112 made the data directory machine-wide
    /// (<c>%ProgramData%\DbDataSync</c> by default) rather than per-user, so a named service account
    /// is no longer guaranteed to have write access to it the way a person's own profile directory
    /// would be. <c>LocalSystem</c> needs no grant — its access already covers a directory anyone just
    /// created, the same as before this phase.
    /// </summary>
    private static void GrantDataDirectoryAccess(string root, string? account)
    {
        if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(root);

        var startInfo = new ProcessStartInfo("icacls") { UseShellExecute = false };
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"{account}:(OI)(CI)M");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine($"  Warning: could not start icacls to grant '{account}' access to '{root}'.");
            return;
        }

        process.WaitForExit();
        Console.WriteLine(process.ExitCode == 0
            ? $"  Granted '{account}' access to '{root}'."
            : $"  Warning: icacls exited {process.ExitCode} granting '{account}' access to '{root}' — " +
              "grant it manually before starting the service.");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown service command '{command}'. Use install, uninstall or status.");
        return 1;
    }

    private static int Sc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Could not start sc.exe.");
            return 1;
        }

        process.WaitForExit();

        // 5 is ERROR_ACCESS_DENIED. Registering a service needs elevation, and an operator who gets
        // an access-denied code back deserves the sentence rather than the number.
        if (process.ExitCode == 5)
        {
            Console.Error.WriteLine(
                "Access denied. Registering or removing a Windows service needs an elevated prompt — " +
                "run this from a command prompt started with 'Run as administrator'.");
        }

        return process.ExitCode;
    }
}
