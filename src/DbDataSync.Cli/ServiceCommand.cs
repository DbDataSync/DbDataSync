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
                "uninstall" => Uninstall(args),
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
                "uninstall" => SystemdService.Uninstall(args),
                "status" => SystemdService.Status(),
                var other => Unknown(other),
            };
        }

        Console.Error.WriteLine(
            "Service registration is Windows and Linux (systemd) only. On macOS, run `dbdatasync serve` " +
            "directly, or use the container image.");
        return 1;
    }

    /// <param name="elevatedOverride">Only for <c>ServiceCommandTests</c> — the same test-only escape
    /// hatch <see cref="SystemdService.Install"/>'s own <c>executableOverride</c> is, and for the same
    /// reason: CI's <c>windows-latest</c> runners are elevated, so the refusal branch below is
    /// unreachable there without one.</param>
    internal static int Install(string[] args, bool? elevatedOverride = null)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this tool's own executable path.");
            return 1;
        }

        // Before anything that touches the machine, because every step below needs elevation and two
        // of them fail badly without it. Phase 136's follow-up doc
        // (planning/todo/follow-up-phase-136-140-...) listed "whether a non-elevated first
        // `service install` can register the Event Log source" as unverified; run on a real
        // non-elevated Windows shell it does not merely fail, it crashes: GrantDataDirectoryAccess
        // first rewrites part of the data directory's ACLs, then EventLog.SourceExists throws
        // SecurityException ("some or all event logs could not be searched") straight out of
        // EnsureSourceRegistered, and the CLI has no top-level handler — so the operator gets an
        // unhandled-exception stack trace, exit code 127, and a half-modified directory, with nothing
        // anywhere saying "run this elevated". Refusing up front is what ToolCommand's Unix path
        // already does ("Needs root. Run: sudo ...").
        // The OperatingSystem.IsWindows() half is for the analyzer, not the logic: this method is only
        // ever reached from Run's own Windows branch, but that branch tests RuntimeInformation.IsOSPlatform,
        // which CA1416 does not read as a guard. The plain check is the one it recognises — the same
        // convention phase 136 settled on, and the same ternary shape ToolCommand.IsElevated already uses.
        var elevated = elevatedOverride ?? (!OperatingSystem.IsWindows() || WindowsElevation.IsAdministrator());

        if (!elevated)
        {
            Console.Error.WriteLine(
                "Needs Administrator. Registering a Windows service, taking ownership of the data " +
                "directory and creating the Event Log source all require elevation.");
            Console.Error.WriteLine(
                "Re-run this command from an elevated prompt (right-click the terminal, Run as " +
                "administrator). Nothing has been changed.");
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

        // Phase 136: registered here, elevated, rather than lazily at the moment a startup failure
        // first needs to write to it — see WindowsServiceEventLog.EnsureSourceRegistered's own doc
        // comment. A registration failure is visible in this command's own output, not silently
        // deferred to the first real failure under the service.
        if (OperatingSystem.IsWindows())
            WindowsServiceEventLog.EnsureSourceRegistered();

        var exitCode = Sc([.. arguments]);
        if (exitCode == 0)
        {
            ServiceRegistration.Write(root, account ?? "LocalSystem", "windows");
            Console.WriteLine("Next: `dbdatasync config check`.");
        }

        return exitCode;
    }

    /// <param name="elevatedOverride">Only for <c>ServiceCommandTests</c> — see <see cref="Install"/>'s
    /// own parameter of the same name.</param>
    internal static int Uninstall(string[] args, bool? elevatedOverride = null)
    {
        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);

        // Same guard as Install, for the quieter half of the same bug. `sc delete` needs elevation, and
        // without this the command answered with sc.exe's own "Access is denied" *after* having already
        // deleted the local registration record below — a failed uninstall that left the service
        // installed and running while telling the rest of the tool it was gone.
        var elevated = elevatedOverride ?? (!OperatingSystem.IsWindows() || WindowsElevation.IsAdministrator());

        if (!elevated)
        {
            Console.Error.WriteLine("Needs Administrator. Removing a Windows service requires elevation.");
            Console.Error.WriteLine(
                "Re-run this command from an elevated prompt (right-click the terminal, Run as " +
                "administrator). Nothing has been changed.");
            return 1;
        }

        var exitCode = Sc("delete", ServiceName);

        // After sc.exe, and only on success. The record is what ReadinessChecks (`config check`) and
        // ServeCommand's own startup-failure message read to report which account a registered service
        // runs as — the diagnostic context phases 135 and 136 added precisely so an Error 1053 names
        // something. Clearing it while the service is still installed does not just lose a file, it
        // makes both of those quietly report a machine that does not exist.
        if (exitCode == 0)
            ServiceRegistration.Clear(root);

        return exitCode;
    }

    /// <summary>
    /// Issuing and granting are one operation from the operator's point of view — the same principle
    /// phase 82 applied to a certificate's private key. Phase 112 made the data directory machine-wide
    /// (<c>%ProgramData%\DbDataSync</c> by default) rather than per-user, so a named service account
    /// is no longer guaranteed to have write access to it the way a person's own profile directory
    /// would be.
    /// <para>
    /// Phase 135: this runs for every account, including <c>LocalSystem</c> — libgit2's ownership-safety
    /// check (the same protection as git's own CVE-2022-24765 <c>safe.directory</c> fix) cares about the
    /// directory's <b>owner</b>, not its ACL, and a directory created by an earlier interactive run is
    /// not owned by <c>LocalSystem</c> just because <c>LocalSystem</c> already has access rights to it.
    /// <c>/setowner</c> runs before <c>/grant</c>, not instead of it — taking ownership alone gives the
    /// new owner implicit <c>WRITE_DAC</c> (the right to change permissions), not necessarily explicit
    /// data access. icacls's own recognized name for <c>LocalSystem</c> is <c>SYSTEM</c>, not the string
    /// used everywhere else in this file.
    /// </para>
    /// </summary>
    internal static void GrantDataDirectoryAccess(string root, string? account)
    {
        Directory.CreateDirectory(root);
        var effectiveAccount = string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? null
            : account;
        effectiveAccount ??= "SYSTEM";

        RunIcacls(root, ["/setowner", effectiveAccount, "/T", "/C"],
            $"take ownership of '{root}' for '{effectiveAccount}'");
        RunIcacls(root, ["/grant", $"{effectiveAccount}:(OI)(CI)M", "/T"],
            $"grant '{effectiveAccount}' access to '{root}'");
    }

    /// <summary>Warns rather than fails — same non-fatal posture as before phase 135: an icacls failure
    /// here is surfaced but does not block <c>service install</c> from registering the service.</summary>
    private static void RunIcacls(string root, IReadOnlyList<string> arguments, string description)
    {
        var startInfo = new ProcessStartInfo("icacls") { UseShellExecute = false };
        startInfo.ArgumentList.Add(root);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine($"  Warning: could not start icacls to {description}.");
            return;
        }

        process.WaitForExit();
        Console.WriteLine(process.ExitCode == 0
            ? $"  Ran icacls to {description}."
            : $"  Warning: icacls exited {process.ExitCode} trying to {description} — do this manually " +
              "before starting the service.");
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
