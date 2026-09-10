using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbDataSync.Cli;

/// <summary>
/// The handful of external operations <see cref="ToolCommand"/> needs — checking elevation, reading and
/// writing the Unix symlink / macOS <c>paths.d</c> file / Windows Machine <c>PATH</c> a machine-wide
/// install touches, and chmod-ing the tool directory — behind one seam, so
/// <c>ToolCommandTests</c> can drive install/uninstall on every platform's path with a fake and prove
/// what it *would* do without writing to this host's real <c>/usr/local/bin</c>, its real Machine
/// <c>PATH</c>, or <c>/etc/paths.d</c>. <see cref="RealToolPathEnvironment"/> is what a production run
/// actually uses.
/// </summary>
internal interface IToolPathEnvironment
{
    bool IsElevated { get; }

    /// <summary>The target a Unix symlink at <paramref name="path"/> currently points at, or null if
    /// nothing is there.</summary>
    string? ReadSymlinkTarget(string path);

    /// <summary>Creates (or replaces, matching <c>ln -sfn</c>) a symlink at <paramref name="path"/>
    /// pointing at <paramref name="target"/>.</summary>
    void CreateSymlink(string path, string target);

    void RemoveSymlink(string path);

    /// <summary><c>chmod -R a+rX</c> — every entry under <paramref name="path"/> becomes
    /// world-readable, and world-executable if it already was (or is a directory).</summary>
    void Chmod(string path);

    string? ReadMachinePath();

    void WriteMachinePath(string value);

    /// <summary>The one line an <c>/etc/paths.d/dbdatasync</c>-style file holds, trimmed, or null if
    /// the file doesn't exist.</summary>
    string? ReadPathsD(string path);

    void WritePathsD(string path, string content);

    void DeletePathsD(string path);
}

internal sealed class RealToolPathEnvironment : IToolPathEnvironment
{
    public bool IsElevated => OperatingSystem.IsWindows() ? IsWindowsAdministrator() : GetEuid() == 0;

    public string? ReadSymlinkTarget(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        return new FileInfo(path).LinkTarget;
    }

    public void CreateSymlink(string path, string target)
    {
        // Matches `ln -sfn`: replace whatever is there, symlink or not, rather than refusing.
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            File.Delete(path);

        File.CreateSymbolicLink(path, target);
    }

    public void RemoveSymlink(string path)
    {
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            File.Delete(path);
    }

    public void Chmod(string path) => ChmodRecursive(path);

    private static void ChmodRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            SetReadAndExecuteForAll(path, alwaysExecutable: true);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                ChmodRecursive(entry);
        }
        else if (File.Exists(path))
        {
            SetReadAndExecuteForAll(path, alwaysExecutable: false);
        }
    }

    /// <summary><c>a+rX</c>: read for everyone, always; execute for everyone only if the entry is a
    /// directory or was already executable by its owner — <c>chmod</c>'s own capital-X semantics,
    /// so this never turns a plain data file into an executable one.
    /// <para>
    /// Only ever reached from <see cref="ToolCommand"/>'s Unix install path (never Windows'), but the
    /// CA1416 platform-compatibility analyzer can't see that guard through the
    /// <see cref="IToolPathEnvironment"/> interface boundary — suppressed rather than threading a
    /// <see cref="SupportedOSPlatformAttribute"/> up through an interface method every future
    /// implementer (a Windows fake included) would then also have to carry.
    /// </para>
    /// </summary>
#pragma warning disable CA1416
    private static void SetReadAndExecuteForAll(string path, bool alwaysExecutable)
    {
        var mode = File.GetUnixFileMode(path);
        var executable = alwaysExecutable || (mode & UnixFileMode.UserExecute) != 0;

        var next = mode | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (executable)
            next |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(path, next);
    }
#pragma warning restore CA1416

    public string? ReadMachinePath() => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);

    public void WriteMachinePath(string value)
    {
        Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.Machine);
        if (OperatingSystem.IsWindows())
            BroadcastEnvironmentChange();
    }

    public string? ReadPathsD(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    public void WritePathsD(string path, string content) => File.WriteAllText(path, content);

    public void DeletePathsD(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // Broadcasts WM_SETTINGCHANGE so already-running processes (Explorer, an open terminal that polls)
    // notice the Machine PATH changed — a brand new process picks it up regardless, this just saves
    // waiting for one. Best-effort: nothing here depends on any process actually receiving it.
    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange() =>
        SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);

    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, string lParam, uint fuFlags, uint uTimeout, out nint result);
}

/// <summary>
/// <c>dbdatasync tool install</c> / <c>tool uninstall</c> (phase 123) — wires a
/// <c>dotnet tool install --tool-path &lt;dir&gt;</c> copy of this CLI onto the machine's <c>PATH</c>:
/// a <c>/usr/local/bin</c> symlink on Linux/other Unix, an <c>/etc/paths.d</c> entry on macOS (which
/// does not honour arbitrary <c>/usr/local/bin</c> symlinks the way Linux shells do), or the Machine
/// <c>PATH</c> on Windows. The binary analog of phase 112's machine-wide *data* directory.
/// <para>
/// Never downloads or restores anything — <c>dotnet tool install|update --tool-path</c> already did
/// that before this ever runs; this only makes the result reachable as a bare <c>dbdatasync</c> for
/// everyone on the machine, the way a per-user <c>dotnet tool install --global</c> already is for the
/// installing user alone.
/// </para>
/// </summary>
internal static class ToolCommand
{
    internal static int Run(string[] args, IToolPathEnvironment? environment = null)
    {
        var env = environment ?? new RealToolPathEnvironment();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "install" => Install(args, env),
            "uninstall" => Uninstall(args, env),
            var other => Unknown(other),
        };
    }

    private static int Install(string[] args, IToolPathEnvironment env)
    {
        var toolDir = ResolveToolDir(args);
        if (toolDir is null)
        {
            Console.Error.WriteLine(
                "Could not determine this tool's own directory. Pass --dir <path> explicitly.");
            return 1;
        }

        WarnIfUnderUserProfile(toolDir);

        return OperatingSystem.IsWindows() ? InstallWindows(toolDir, env) : InstallUnix(toolDir, env);
    }

    private static int InstallUnix(string toolDir, IToolPathEnvironment env)
    {
        var executable = Path.Combine(toolDir, "dbdatasync");

        if (!env.IsElevated)
        {
            Console.Error.WriteLine($"Needs root. Run: sudo {executable} tool install");
            return 1;
        }

        env.Chmod(toolDir);

        if (OperatingSystem.IsMacOS())
        {
            const string pathsDFile = "/etc/paths.d/dbdatasync";
            if (env.ReadPathsD(pathsDFile) == toolDir)
            {
                Console.WriteLine($"'{pathsDFile}' already points at '{toolDir}'.");
                return 0;
            }

            env.WritePathsD(pathsDFile, toolDir + "\n");
            Console.WriteLine($"Wrote '{pathsDFile}' -> '{toolDir}'.");
            Console.WriteLine("Open a new terminal to pick it up. Next: `dbdatasync config check`.");
            return 0;
        }

        const string linkPath = "/usr/local/bin/dbdatasync";
        if (env.ReadSymlinkTarget(linkPath) == executable)
        {
            Console.WriteLine($"'{linkPath}' already links to '{executable}'.");
            return 0;
        }

        env.CreateSymlink(linkPath, executable);
        Console.WriteLine($"Linked '{linkPath}' -> '{executable}'.");
        Console.WriteLine("Next: `dbdatasync config check`.");
        return 0;
    }

    private static int InstallWindows(string toolDir, IToolPathEnvironment env)
    {
        if (!env.IsElevated)
        {
            Console.Error.WriteLine(
                $"Needs an elevated prompt. Run (elevated): \"{Path.Combine(toolDir, "dbdatasync.exe")}\" tool install");
            return 1;
        }

        var (path, alreadyPresent) = AddToPath(env.ReadMachinePath(), toolDir);
        if (alreadyPresent)
        {
            Console.WriteLine($"'{toolDir}' is already on the machine PATH.");
            return 0;
        }

        env.WriteMachinePath(path);
        Console.WriteLine($"Added '{toolDir}' to the machine PATH.");
        Console.WriteLine("Open a new terminal to pick it up. Next: `dbdatasync config check`.");
        return 0;
    }

    private static int Uninstall(string[] args, IToolPathEnvironment env)
    {
        var toolDir = ResolveToolDir(args);
        if (toolDir is null)
        {
            Console.Error.WriteLine(
                "Could not determine this tool's own directory. Pass --dir <path> explicitly.");
            return 1;
        }

        return OperatingSystem.IsWindows() ? UninstallWindows(toolDir, env) : UninstallUnix(toolDir, env);
    }

    private static int UninstallUnix(string toolDir, IToolPathEnvironment env)
    {
        if (!env.IsElevated)
        {
            Console.Error.WriteLine("Needs root. Run: sudo dbdatasync tool uninstall");
            return 1;
        }

        if (OperatingSystem.IsMacOS())
        {
            const string pathsDFile = "/etc/paths.d/dbdatasync";
            if (env.ReadPathsD(pathsDFile) is null)
            {
                Console.WriteLine($"'{pathsDFile}' does not exist — nothing to remove.");
            }
            else
            {
                env.DeletePathsD(pathsDFile);
                Console.WriteLine($"Removed '{pathsDFile}'.");
            }
        }
        else
        {
            const string linkPath = "/usr/local/bin/dbdatasync";
            if (env.ReadSymlinkTarget(linkPath) is null)
            {
                Console.WriteLine($"'{linkPath}' does not exist — nothing to remove.");
            }
            else
            {
                env.RemoveSymlink(linkPath);
                Console.WriteLine($"Removed '{linkPath}'.");
            }
        }

        // The --tool-path payload is deliberately untouched — this command only ever managed the
        // PATH-facing pointer to it.
        Console.WriteLine($"Run `dotnet tool uninstall --tool-path {toolDir} DbDataSync` to finish removal.");
        return 0;
    }

    private static int UninstallWindows(string toolDir, IToolPathEnvironment env)
    {
        if (!env.IsElevated)
        {
            Console.Error.WriteLine("Needs an elevated prompt. Run (elevated): dbdatasync tool uninstall");
            return 1;
        }

        var (path, removed) = RemoveFromPath(env.ReadMachinePath(), toolDir);
        if (!removed)
        {
            Console.WriteLine($"'{toolDir}' is not on the machine PATH — nothing to remove.");
        }
        else
        {
            env.WriteMachinePath(path);
            Console.WriteLine($"Removed '{toolDir}' from the machine PATH.");
        }

        Console.WriteLine($"Run `dotnet tool uninstall --tool-path {toolDir} DbDataSync` to finish removal.");
        return 0;
    }

    private static string? ResolveToolDir(string[] args)
    {
        var explicitDir = CliOptions.Read(args, "--dir");
        if (explicitDir is not null)
            return Path.GetFullPath(explicitDir);

        var processPath = Environment.ProcessPath;
        return processPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(processPath));
    }

    private static void WarnIfUnderUserProfile(string toolDir)
    {
        if (!CliOptions.IsUnderUserProfile(toolDir))
            return;

        Console.WriteLine(
            $"'{toolDir}' looks like a per-user install; the docs point a machine-wide install at " +
            $"'{CliOptions.DefaultToolDir}' — see docs/install.md.");
    }

    /// <summary>Pure, so it's directly unit-testable: appends <paramref name="toolDir"/> to
    /// <paramref name="currentPath"/> unless an entry already matches it case-insensitively and modulo
    /// a trailing separator.</summary>
    internal static (string Path, bool AlreadyPresent) AddToPath(string? currentPath, string toolDir)
    {
        var current = currentPath ?? "";
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => PathEntryMatches(e, toolDir)))
            return (current, true);

        var next = entries.Length == 0 ? toolDir : string.Join(';', entries) + ";" + toolDir;
        return (next, false);
    }

    /// <summary>The inverse of <see cref="AddToPath"/> — drops every entry matching
    /// <paramref name="toolDir"/>, leaving the rest byte-for-byte (same order, same entries).</summary>
    internal static (string Path, bool Removed) RemoveFromPath(string? currentPath, string toolDir)
    {
        var current = currentPath ?? "";
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var remaining = entries.Where(e => !PathEntryMatches(e, toolDir)).ToArray();
        return remaining.Length == entries.Length
            ? (current, false)
            : (string.Join(';', remaining), true);
    }

    private static bool PathEntryMatches(string entry, string toolDir) =>
        string.Equals(
            entry.TrimEnd('\\', '/'), toolDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown tool subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine("""
            Usage:
              dbdatasync tool install   [--dir <path>]
              dbdatasync tool uninstall [--dir <path>]
            """);
}
