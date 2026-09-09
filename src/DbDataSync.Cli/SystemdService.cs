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
    internal const string UnitPath = "/etc/systemd/system/dbdatasync.service";

    /// <summary>
    /// Matches <see cref="CliOptions.DefaultRoot"/>'s own Linux answer (phase 112) exactly — repeated
    /// as a literal here (not a reference) because this is specifically the one path systemd's own
    /// <c>StateDirectory=</c> directive can manage, which is a Linux-service-unit concept unrelated to
    /// how the CLI resolves its default; a future change to one should not silently change the other.
    /// </summary>
    private const string ManagedStateDirectoryRoot = "/var/lib/dbdatasync";

    internal static int Install(string[] args, ISystemdEnvironment? environment = null)
    {
        var env = environment ?? new RealSystemdEnvironment();

        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this tool's own executable path.");
            return 1;
        }

        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);
        var url = CliOptions.Read(args, "--url") ?? "http://localhost:5080";
        var user = CliOptions.Read(args, "--user") ?? "dbdatasync";

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
            env.WriteUnitFile(UnitPath, RenderUnit(executable, root, url, user));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not write '{UnitPath}': {ex.Message}");
            PrintPermissionSentence("register a systemd service");
            return 1;
        }

        env.ChownRecursive(root, user, user);

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

        // Enabled, not started — the same "registers but does not start" shape
        // `sc create ... start= auto` leaves Windows in, so the operator sees the first start's own
        // output rather than it scrolling by during `install`.
        Console.WriteLine();
        Console.WriteLine($"Run `sudo systemctl start {UnitName}` to start it now.");
        return 0;
    }

    internal static int Uninstall(ISystemdEnvironment? environment = null)
    {
        var env = environment ?? new RealSystemdEnvironment();

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
    internal static string RenderUnit(string executable, string root, string url, string user)
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

        return $"""
            [Unit]
            Description=DbDataSync — cross-database replication
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            ExecStart={execStart}
            User={user}
            Group={user}
            WorkingDirectory={root}
            Restart=on-failure
            RestartSec=5
            {serviceExtras}

            [Install]
            WantedBy=multi-user.target

            """;
    }

    private static string QuoteForSystemd(string value) => $"\"{value}\"";

    /// <summary>The Linux counterpart of the Windows path's <c>ERROR_ACCESS_DENIED</c> (5) → sentence
    /// — writing under <c>/etc/systemd/system</c>, creating a system user, and <c>daemon-reload</c>
    /// all need root, and an operator who hits that deserves the sentence, not a bare exit code.</summary>
    private static void PrintPermissionSentence(string action) =>
        Console.Error.WriteLine(
            $"Could not {action} — registering a systemd service needs root. Re-run with " +
            "`sudo dbdatasync service install ...`.");
}
