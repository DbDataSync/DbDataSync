using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DbDataSync.Cli;

/// <summary>
/// Registers this tool as a Windows service or (phase 111) a Linux systemd service, and says what it
/// did.
/// <para>
/// The binary registered is **the tool's own apphost shim** — the <c>dbdatasync.exe</c>/apphost the
/// SDK put in the user's tools directory — with <c>serve</c> and fully resolved paths as its
/// arguments. Resolved at install time and printed, because a service has no console to say "I could
/// not find my config repository" on: the moment to find that out is now.
/// </para>
/// <para>
/// The systemd path lives in its own file (<see cref="SystemdService"/>), the same way this file's
/// own <c>sc.exe</c> mechanics stay separate from it — each platform's machinery is separable, even
/// though both are reached through this one command.
/// </para>
/// </summary>
public static class ServiceCommand
{
    public const string ServiceName = "DbDataSync";

    public static int Run(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: dbdatasync service install|uninstall|status");
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "install" => SystemdService.Install(args),
                "uninstall" => SystemdService.Uninstall(),
                "status" => SystemdService.Status(),
                var other => Unknown(other),
            };
        }

        Console.Error.WriteLine(
            "Service registration is Windows and Linux (systemd) only. On macOS, run `dbdatasync serve` " +
            "directly, or use the container image.");
        return 1;
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

        // A warning, not a refusal — sc.exe has no equivalent of systemd's ProtectHome, so a
        // user-profile executable does not fail the service the way it can on Linux (SystemdService's
        // own hard error). It still breaks the moment that profile is cleaned up or the service is
        // re-registered from another account.
        if (CliOptions.IsUnderUserProfile(executable))
        {
            Console.WriteLine(
                $"Warning: '{executable}' is installed in a user profile — a service pointing here " +
                "breaks if that profile is removed, or you re-register from another account. Install " +
                "machine-wide first:");
            Console.WriteLine($"    dotnet tool install --tool-path \"{CliOptions.DefaultToolDir}\" DbDataSync");
            Console.WriteLine($"    \"{CliOptions.DefaultToolDir}\\dbdatasync.exe\" tool install");
            Console.WriteLine();
        }

        var binPath = $"\"{executable}\" serve --repo \"{root}\" --url {url}";

        // sc.exe's parser wants each `key=` and its value as two SEPARATE argv tokens — exactly what
        // typing them at a cmd.exe prompt produces, since the unescaped space between "start=" and
        // "auto" is what cmd splits on. ArgumentList doesn't do that splitting: a single entry like
        // $"start= {value}" survives as one atomic argument and gets wrapped in quotes because it
        // contains a space, so sc.exe never sees a token that's exactly "start=" — its lookahead for
        // the enum value grabs the *next* real argument instead and fails validation on it, which is
        // exactly the "Invalid start= field" sc.exe reports. binPath is the one field that tolerated
        // this: it's free-form, not an enum sc.exe checks, so the extra text just became part of its
        // value. Every key and its value now gets its own list entry, matching cmd's own tokenization.
        var arguments = new List<string>
        {
            "create", ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", "DbDataSync",
        };

        if (account is not null)
        {
            arguments.Add("obj=");
            arguments.Add(account);
        }

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

        var exitCode = Sc([.. arguments]);
        if (exitCode == 0)
            Console.WriteLine("Next: `dbdatasync config check`.");
        return exitCode;
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
