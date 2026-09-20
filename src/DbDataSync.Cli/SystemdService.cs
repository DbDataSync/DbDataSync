using System.Diagnostics;

namespace DbDataSync.Cli;

/// <summary>
/// The handful of external operations <see cref="SystemdService"/> needs — writing the unit file,
/// creating the service's system user, and running <c>systemctl</c> — behind one seam, so
/// <c>ServiceCommandTests</c> can drive the whole install/uninstall/status flow with a fake and prove
/// what it *would* do without writing to this host's real <c>/etc/systemd/system</c>, creating a real
/// system user, or touching its real systemd. <see cref="RealSystemdEnvironment"/> is what a
/// production run actually uses.
/// </summary>
internal interface ISystemdEnvironment
{
    bool UserExists(string user);
    int CreateSystemUser(string user);
    void WriteUnitFile(string path, string content);
    void DeleteUnitFile(string path);
    void ChownRecursive(string path, string user, string group);
    int RunSystemctl(params string[] args);
}

internal sealed class RealSystemdEnvironment : ISystemdEnvironment
{
    public bool UserExists(string user) => RunProcess("id", ["-u", user]) == 0;

    public int CreateSystemUser(string user) =>
        RunProcess("useradd", ["--system", "--no-create-home", "--shell", "/usr/sbin/nologin", user]);

    public void WriteUnitFile(string path, string content) => File.WriteAllText(path, content);

    public void DeleteUnitFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void ChownRecursive(string path, string user, string group) =>
        RunProcess("chown", ["-R", $"{user}:{group}", path]);

    public int RunSystemctl(params string[] args) => RunProcess("systemctl", args);

    private static int RunProcess(string fileName, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process is null)
            return -1;

        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>
/// Linux systemd unit registration (phase 111) — the Linux analog of <see cref="ServiceCommand"/>'s
/// own <c>sc.exe</c> path. A dedicated, named system user (<c>dbdatasync</c> by default) is the Linux
/// analog of <c>LocalSystem</c>: predictable, and it maps onto the mental model a Windows operator
/// already has from <c>--account</c>. <c>DynamicUser=yes</c> (systemd's modern per-start ephemeral
/// UID) was considered and set aside — it interacts badly with a git-tracked repo an operator also
/// hand-edits, and with a <c>--repo</c> outside the default location.
/// </summary>
internal static class SystemdService
{
    internal const string UnitName = "dbdatasync";

    /// <summary>Set in the unit's environment by <see cref="RenderUnit"/> when it is asked for self-update
    /// (<c>service install --self-update</c>). The service reads it to know it is running under a unit that
    /// applies updates before starting, and <c>config check</c> looks for the same text in an installed unit file.
    /// <para>
    /// **A root-level opt-in, not a default.** The unit step it enables runs as root, so it is only ever written
    /// by someone running <c>service install</c> as root and asking for it — a configuration setting the service
    /// itself can write would not be a decision root made.
    /// </para></summary>
    internal const string SelfUpdateMarker = "DBDATASYNC_SELF_UPDATE=1";

    internal const string SelfUpdateEnvironmentVariable = "DBDATASYNC_SELF_UPDATE";

    /// <summary>The exit code a service uses to ask systemd to restart it so an update can be applied.</summary>
    internal const int SelfUpdateExitCode = 75;
    internal const string UnitPath = "/etc/systemd/system/dbdatasync.service";

    /// <summary>
    /// Matches <see cref="CliOptions.DefaultRoot"/>'s own Linux answer (phase 112) exactly — repeated
    /// as a literal here (not a reference) because this is specifically the one path systemd's own
    /// <c>StateDirectory=</c> directive can manage, which is a Linux-service-unit concept unrelated to
    /// how the CLI resolves its default; a future change to one should not silently change the other.
    /// </summary>
    private const string ManagedStateDirectoryRoot = "/var/lib/dbdatasync";

    /// <param name="executableOverride">Only for <c>SystemdServiceTests</c> — a real run always takes
    /// <see cref="Environment.ProcessPath"/>, which cannot be pointed at an arbitrary path to prove
    /// the user-profile/hardened-unit refusal below without actually installing this tool under
    /// <c>/home</c> first.</param>
    internal static int Install(string[] args, ISystemdEnvironment? environment = null, string? executableOverride = null)
    {
        var env = environment ?? new RealSystemdEnvironment();

        var executable = executableOverride ?? Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this tool's own executable path.");
            return 1;
        }

        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";
        var user = CliOptions.Read(args, "--user") ?? "dbdatasync";
        var selfUpdate = CliOptions.Has(args, "--self-update");
        var isManagedRoot = string.Equals(root, ManagedStateDirectoryRoot, StringComparison.Ordinal);

        // A hardened unit's own ProtectHome=yes (below) hides a user-profile ExecStart from the
        // service — it would install cleanly and fail on first start, the exact bug phase 123 exists
        // to catch before an operator hits it. A non-hardened unit still gets a warning: nothing stops
        // it starting today, but the same profile-cleanup/re-registration risk applies.
        if (CliOptions.IsUnderUserProfile(executable))
        {
            if (isManagedRoot)
            {
                Console.Error.WriteLine(
                    $"'{executable}' is installed in a user profile, and this unit hardens with " +
                    "ProtectHome=yes (the default --repo hides user-profile paths from the service) — " +
                    "it would install but fail to start. Install dbdatasync machine-wide first:");
                Console.Error.WriteLine($"    sudo dotnet tool install --tool-path {CliOptions.DefaultToolDir} DbDataSync");
                Console.Error.WriteLine($"    sudo {CliOptions.DefaultToolDir}/dbdatasync tool install");
                return 1;
            }

            Console.WriteLine(
                $"Warning: '{executable}' is installed in a user profile — a service pointing here " +
                "breaks if that profile is removed, or you re-register from another account. Install " +
                "machine-wide first:");
            Console.WriteLine($"    sudo dotnet tool install --tool-path {CliOptions.DefaultToolDir} DbDataSync");
            Console.WriteLine($"    sudo {CliOptions.DefaultToolDir}/dbdatasync tool install");
            Console.WriteLine();
        }

        if (!env.UserExists(user))
        {
            if (env.CreateSystemUser(user) != 0)
            {
                PrintPermissionSentence($"create system user '{user}'");
                return 1;
            }

            Console.WriteLine($"Created system user '{user}'.");
        }

        try
        {
            Directory.CreateDirectory(root);
            env.WriteUnitFile(UnitPath, RenderUnit(executable, root, url, user, ResolveDotnetRoot(), selfUpdate));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not write '{UnitPath}': {ex.Message}");
            PrintPermissionSentence("register a systemd service");
            return 1;
        }

        env.ChownRecursive(root, user, user);

        if (selfUpdate)
        {
            Console.WriteLine(
                "Self-update is on for this unit: before every start it runs a privileged step that applies an update the " +
                "service asked for, and rolls one back that never became healthy. Updating from the web console also needs " +
                "DbDataSync:SelfUpdateEnabled.");
            Console.WriteLine();
        }

        Console.WriteLine($"Registering the '{UnitName}' systemd service:");
        Console.WriteLine($"  executable         {executable}");
        Console.WriteLine($"  config repository  {Path.Combine(root, "config")}");
        Console.WriteLine($"  console            {url}");
        Console.WriteLine($"  user               {user}");

        // Not cosmetic: a connection using IntegratedAuth authenticates as this account's own Kerberos
        // credentials/keytab, if any — the same reason ServiceCommand's Windows Install prints this.
        Console.WriteLine();
        Console.WriteLine(
            "  Connections using integrated authentication authenticate as this account's Kerberos " +
            "credentials, if any.");

        var reloadExit = env.RunSystemctl("daemon-reload");
        if (reloadExit != 0)
            return reloadExit;

        var enableExit = env.RunSystemctl("enable", UnitName);
        if (enableExit != 0)
            return enableExit;

        ServiceRegistration.Write(root, user, "linux");

        // Enabled, not started — the same "registers but does not start" shape
        // `sc create ... start= auto` leaves Windows in, so the operator sees the first start's own
        // output rather than it scrolling by during `install`.
        Console.WriteLine();
        Console.WriteLine($"Run `sudo systemctl start {UnitName}` to start it now.");
        Console.WriteLine("Next: `dbdatasync config check`.");
        return 0;
    }

    internal static int Uninstall(string[] args, ISystemdEnvironment? environment = null)
    {
        var env = environment ?? new RealSystemdEnvironment();

        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);
        ServiceRegistration.Clear(root);

        var disableExit = env.RunSystemctl("disable", "--now", UnitName);
        env.DeleteUnitFile(UnitPath);
        var reloadExit = env.RunSystemctl("daemon-reload");

        Console.WriteLine($"Uninstalled the '{UnitName}' service. Its repository and state are untouched.");
        return disableExit != 0 ? disableExit : reloadExit;
    }

    internal static int Status(ISystemdEnvironment? environment = null) =>
        (environment ?? new RealSystemdEnvironment()).RunSystemctl("status", UnitName, "--no-pager");

    /// <summary>
    /// Pure — no I/O, no process calls — so this is directly unit-testable. <c>Type=notify</c> because
    /// <c>DbDataSyncHost.Build</c>'s own <c>UseSystemd()</c> call sends <c>sd_notify(READY=1)</c> once
    /// the host has actually started, which is what lets <c>systemctl start</c> block until the
    /// process is really serving rather than merely existing.
    /// </summary>
    /// <param name="dotnetRoot">
    /// Set as <c>DOTNET_ROOT</c> in the unit's own environment when not null (phase 123) — fixes the
    /// case where <c>dotnet</c> was installed by <c>dotnet-install.sh</c> into a user's <c>~/.dotnet</c>
    /// and so is invisible to a <c>nologin</c> service account's own <c>PATH</c>. Scoped to this one
    /// unit; no machine-global side effect. Null omits the line entirely rather than emitting a wrong
    /// one — see <see cref="ResolveDotnetRoot"/>.
    /// </param>
    internal static string RenderUnit(string executable, string root, string url, string user, string? dotnetRoot = null, bool selfUpdate = false)
    {
        var execStart = $"{QuoteForSystemd(executable)} serve --repo {QuoteForSystemd(root)} --url {url}";
        var isManagedRoot = string.Equals(root, ManagedStateDirectoryRoot, StringComparison.Ordinal);

        var serviceExtras = isManagedRoot
            ? $"""
               StateDirectory={UnitName}
               NoNewPrivileges=yes
               ProtectSystem=strict
               ProtectHome=yes
               ReadWritePaths={root}
               """
            : $"""
               # --repo is outside {ManagedStateDirectoryRoot}, so the default hardening
               # (ProtectSystem=strict, which would make this path read-only) is skipped rather than
               # silently breaking access to an operator's own chosen location.
               ReadWritePaths={root}
               """;

        var dotnetRootLine = dotnetRoot is null ? "" : $"Environment=DOTNET_ROOT={dotnetRoot}\n";
        var selfUpdateEnvironment = selfUpdate ? $"Environment={SelfUpdateMarker}\n" : "";
        var selfUpdateStep = selfUpdate
            ? $"# `-` so that a step which fails, or does not exist (an update can go back to a version older than this feature),\n# never stops the service starting on whatever is installed; `+` so it runs outside this unit's own sandbox and\n# User=, which is what lets it write the tool directory. Both checked on systemd 255.\nExecStartPre=-+{QuoteForSystemd(executable)} internal apply-update --repo {QuoteForSystemd(root)}\n"
            : "";
        var selfUpdateExit = selfUpdate
            ? $"SuccessExitStatus={SelfUpdateExitCode}\nRestartForceExitStatus={SelfUpdateExitCode}\n"
            : "";

        return $"""
            [Unit]
            Description=DbDataSync — cross-database replication
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            {dotnetRootLine}{selfUpdateEnvironment}{selfUpdateStep}ExecStart={execStart}
            User={user}
            Group={user}
            WorkingDirectory={root}
            Restart=on-failure
            RestartSec=5
            {selfUpdateExit}{serviceExtras}

            [Install]
            WantedBy=multi-user.target

            """;
    }

    private static string QuoteForSystemd(string value) => $"\"{value}\"";

    /// <summary>
    /// <c>DOTNET_ROOT</c> from the environment first — an operator who already set it clearly meant
    /// it. Otherwise, walked up from <see cref="System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory"/>
    /// (<c>.../shared/Microsoft.NETCore.App/&lt;ver&gt;/</c>) three levels to the dotnet root every
    /// layout this ships on shares (an apt/package-manager install, a tarball, <c>dotnet-install.sh</c>).
    /// Verified against an actual <c>dotnet</c>/<c>dotnet.exe</c> at that root before trusting it —
    /// null (omitting the line) rather than a confidently wrong path if the walk-up ever doesn't land
    /// where expected.
    /// </summary>
    internal static string? ResolveDotnetRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(fromEnvironment))
            return fromEnvironment;

        var dir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        for (var i = 0; i < 3 && dir?.Parent is not null; i++)
            dir = dir.Parent;

        if (dir is null)
            return null;

        var dotnetExecutable = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(dotnetExecutable) ? dir.FullName : null;
    }

    /// <summary>The Linux counterpart of the Windows path's <c>ERROR_ACCESS_DENIED</c> (5) → sentence
    /// — writing under <c>/etc/systemd/system</c>, creating a system user, and <c>daemon-reload</c>
    /// all need root, and an operator who hits that deserves the sentence, not a bare exit code.</summary>
    private static void PrintPermissionSentence(string action) =>
        Console.Error.WriteLine(
            $"Could not {action} — registering a systemd service needs root. Re-run with " +
            "`sudo dbdatasync service install ...`.");
}
