using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync setup</c> — an interactive walk-through for a fresh install, or a review screen for
/// an existing one. Everything it writes goes through the same <see cref="DbDataSyncConfigFile"/> and
/// <see cref="SecretStore"/> calls a scripted deployment already uses (<c>SetValue</c>, <c>secret
/// set</c>); this exists to save a first-time operator from having to know CONFIG.md by heart, not to
/// add a configuration path nothing else uses.
/// </summary>
public static class SetupCommand
{
    public static Task<int> RunAsync(string[] args, IPromptIo io) =>
        RunAsync(args, io, LibraryInstaller.InstallAsync);

    /// <summary>
    /// The <paramref name="installLibrary"/> seam exists only for <c>SetupCommandTests</c> — a real
    /// run always passes <see cref="LibraryInstaller.InstallAsync"/>, which shells out to
    /// <c>dotnet publish</c> and would make every driver-step test a network call.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args, IPromptIo io,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        if (!io.IsInteractive)
        {
            io.WriteLine(
                "setup is interactive — run `dbdatasync config check` to check a configuration, or " +
                $"edit {DbDataSyncConfigFile.FileName} directly (see CONFIG.md).");
            return 1;
        }

        var prompt = new Prompt(io);
        var candidate = DbDataSyncRoot.Resolve(args);
        var root = candidate;

        if (!ExistingSetup.DetectedAt(root))
        {
            // Phase 112: the platform default is machine-wide now, and the operator's only real
            // configuration may still be sitting at the old per-user one. Default the folder prompt
            // to it instead of guessing silently — Enter does the sensible thing (review it in
            // place), typing something else starts fresh at the new location deliberately.
            var legacyRoot = LegacyRootMigration.DetectAt(candidate);
            if (legacyRoot is not null)
            {
                io.WriteLine(LegacyRootMigration.Message(legacyRoot, candidate));
                io.WriteLine("");
            }

            root = Path.GetFullPath(prompt.Text("Config folder", legacyRoot ?? candidate));
            if (ExistingSetup.DetectedAt(root))
                io.WriteLine("That folder already has a DbDataSync configuration.");
            else
                return await WalkThroughAsync(root, prompt, io, installLibrary);
        }

        return await ReviewAsync(root, prompt, io);
    }

    private static async Task<int> WalkThroughAsync(
        string root, Prompt prompt, IPromptIo io,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        io.WriteLine($"Setting up DbDataSync at '{root}'.");

        // Step 1 — root: the same idempotent git-init + starter file every `serve` first run does.
        ServeCommand.Prepare(root);

        // Step 2 — console URL.
        string? host = null;
        string url;
        if (prompt.YesNo("Reachable at a hostname other than localhost?", false))
        {
            host = prompt.Text("Hostname");
            var port = prompt.Text("Port", "5080");
            url = $"https://{host}:{port}";
        }
        else
        {
            url = prompt.Text("Console URL", "http://localhost:5080");
        }
        DbDataSyncConfigFile.SetValue(root, "DbDataSync", "Url", url);

        // Step 3 — state database.
        var engine = prompt.Choice(
            "State database",
            [
                (StateEngineIds.Sqlite, "SQLite (default — no separate server)"),
                (StateEngineIds.MsSql, "SQL Server"),
                (StateEngineIds.Postgres, "PostgreSQL"),
            ],
            StateEngineIds.Sqlite);

        string? stateConnectionString = null;
        if (engine != StateEngineIds.Sqlite)
        {
            stateConnectionString = prompt.Text($"{engine} connection string (no password)");
            var password = prompt.Secret("State database password");
            DbDataSyncConfigFile.SetValue(root, "DbDataSync", "StateEngine", engine);
            DbDataSyncConfigFile.SetValue(root, "DbDataSync", "StateConnectionString", stateConnectionString);
            new SecretStore("DbDataSync", true).Store(SecretRefs.ForAppSetting("stateConnectionString"), password);

            // Installing the library itself is skipped here — before phase 109g lands,
            // Microsoft.Data.SqlClient and Npgsql are still hard references, so there is nothing this
            // step would need to restore. `dbdatasync config check` still reports whether the
            // connection opens.
            try
            {
                StateDatabase.FromOptions(
                    engine, Path.Combine(root, "state.db"), stateConnectionString, new SecretStore("DbDataSync", true));
                io.WriteLine("Connected — schema is current.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                or System.Data.Common.DbException)
            {
                io.WriteLine($"Could not connect yet: {ex.Message}");
                io.WriteLine("You can fix this and re-check with `dbdatasync config check`.");
            }
        }

        // Step 4 — additional drivers. The three built-ins need nothing; MySQL/MariaDB has a starter
        // template (109d) this can drive end to end. Anything else is pointed at the manual command —
        // there is no starter template for it to fill in.
        var selected = prompt.MultiChoice(
            "Additional drivers to install now (built-ins need nothing)",
            [
                ("mssql", "SQL Server (built in)"),
                ("postgres", "PostgreSQL (built in)"),
                ("duckdb", "DuckDB (built in)"),
                ("mysql", "MySQL / MariaDB"),
                ("other", "Another engine (configure later)"),
            ]);

        foreach (var driver in selected)
        {
            if (driver == "mysql")
                await InstallMySqlDriverAsync(root, prompt, io, installLibrary);
            else if (driver == "other")
                io.WriteLine(
                    "For any other engine, run `dbdatasync config driver install <id> --library <name> " +
                    "--version <v> [--from mysql]` once this finishes.");
        }

        // Step 5 — authentication.
        var authChoice = prompt.Choice(
            "Authentication",
            [
                ("passkeys", "Passkeys (recommended)"),
                ("windows", "Windows groups"),
                ("none", "None — trusted network only"),
            ],
            "passkeys");

        if (authChoice == "passkeys")
        {
            var relyingPartyId = prompt.Text("Relying party id (bare domain)", host ?? "localhost");
            var origins = new List<string> { url };
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", relyingPartyId);
            DbDataSyncConfigFile.SetListValue(root, "DbDataSync:Auth:Passkeys", "Origins", origins);

            var problem = new PasskeyOptions
            {
                RelyingPartyId = relyingPartyId,
                RelyingPartyName = "DbDataSync",
                Origins = origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
            }.Problem();
            if (problem is not null)
                io.WriteLine($"Warning: {problem}");
        }
        else if (authChoice == "windows")
        {
            var adminGroup = prompt.Text("Admin group");
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "AdminGroup", adminGroup);
            var viewerGroup = prompt.Text("Viewer group (blank for none)", "");
            if (viewerGroup.Length > 0)
                DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "ViewerGroup", viewerGroup);
        }
        else
        {
            var typed = prompt.HardConfirm("DbDataSync will accept every request with no sign-in.", "ALLOW");
            if (string.Equals(typed, "ALLOW", StringComparison.Ordinal))
            {
                DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "Disabled", "true");
            }
            else
            {
                io.WriteLine("Not disabled — falling back to passkeys at localhost.");
                DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", "localhost");
            }
        }

        // Step 6 — service registration: Windows (sc.exe) or Linux (systemd, phase 111). Neither is
        // orchestrated programmatically — both need real elevation this process cannot assume it has —
        // so setup prints the exact command to run rather than guessing at behaviour nothing here can
        // verify without actually elevating mid-session.
        if (OperatingSystem.IsWindows())
        {
            if (prompt.YesNo("Register dbdatasync as a Windows service?", false))
            {
                var account = prompt.Text("Service account", "LocalSystem");
                io.WriteLine("Run this in an elevated prompt to finish:");
                io.WriteLine($"    dbdatasync service install --repo \"{root}\" --url {url} --account {account}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (prompt.YesNo("Register dbdatasync as a systemd service?", false))
            {
                var user = prompt.Text("Service user", "dbdatasync");
                io.WriteLine("Run this as root to finish:");
                io.WriteLine($"    sudo dbdatasync service install --repo \"{root}\" --url {url} --user {user}");
            }
        }

        // Step 7 — certificate: the Windows store, or (any other platform, phase 113) a file.
        if (OperatingSystem.IsWindows())
        {
            if (prompt.YesNo("Set up the TLS certificate?", false))
            {
                var certChoice = prompt.Choice(
                    "Certificate",
                    [
                        ("self-signed", "Self-signed"),
                        ("enroll", "Enroll from an AD CS template"),
                        ("bind", "Bind an existing certificate by thumbprint"),
                    ],
                    "self-signed");

                io.WriteLine("Run the matching command, then bind the thumbprint it prints:");
                io.WriteLine(certChoice switch
                {
                    "self-signed" => $"    dbdatasync config cert new-self-signed --dns {host ?? "localhost"}",
                    "enroll" => "    dbdatasync config cert enroll --template <template>",
                    _ => "    dbdatasync config cert bind --thumbprint <thumbprint>",
                });
            }
        }
        else if (prompt.YesNo("Point Kestrel at a certificate file (PEM), e.g. from certbot?", false))
        {
            // Cross-platform (phase 113), unlike the Windows steps above — printed as a command to run
            // rather than invoked here anyway, matching how every other cert step in this walk-through
            // works, and because the path an operator gives here is exactly what `use-pem` itself
            // still needs to validate before writing anything.
            var certPath = prompt.Text("Certificate file (PEM)");
            var keyPath = prompt.Text("Private key file (PEM, unencrypted)");
            io.WriteLine("Run this to finish:");
            io.WriteLine($"    dbdatasync config cert use-pem --cert \"{certPath}\" --key \"{keyPath}\" --repo \"{root}\"");
        }

        // Step 8 — finish.
        new GitCommitService(root).CommitChanges(
            [DbDataSyncConfigFile.PathIn(root)], "dbdatasync setup", CurrentUser.SystemAuthor);

        io.WriteLine("");
        io.WriteLine("Setup is complete.");
        io.WriteLine($"  config repository  {Path.Combine(root, "config")}");
        io.WriteLine($"  console            {url}");
        io.WriteLine(
            $"Once started, the first sign-in link is printed and saved to " +
            $"'{Path.Combine(root, "FIRST-RUN.txt")}'.");

        if (prompt.YesNo("Start DbDataSync now?", true))
            return await ServeCommand.RunAsync(["--repo", root, "--url", url]);

        return 0;
    }

    private static async Task InstallMySqlDriverAsync(
        string root, Prompt prompt, IPromptIo io,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        const string packageId = "MySqlConnector";
        var version = prompt.Text($"{packageId} version");
        var factoryType = KnownLibraries.TryGet(packageId)!;
        var package = new PackageRef(packageId, version);

        try
        {
            await installLibrary(root, packageId, [package], factoryType, null, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            io.WriteLine($"Could not install '{packageId}': {ex.Message}");
            return;
        }

        var driverDir = Path.Combine(root, "drivers", "mysql");
        var yamlPath = Path.Combine(driverDir, DriverLoader.DescriptorFileName);
        if (File.Exists(yamlPath))
        {
            io.WriteLine($"'{yamlPath}' already exists — left untouched.");
            return;
        }

        Directory.CreateDirectory(driverDir);
        var yaml = DriverTemplates.Render("mysql", "mysql", "MySQL / MariaDB", packageId);
        await File.WriteAllTextAsync(yamlPath, yaml);
        io.WriteLine($"Installed '{packageId}' and wrote '{yamlPath}'. Review the type map before relying on it.");
    }

    private static async Task<int> ReviewAsync(string root, Prompt prompt, IPromptIo io)
    {
        while (true)
        {
            var context = ReadinessChecks.BuildContext(["--repo", root]);
            var results = await ReadinessChecks.RunChecksAsync(context);
            foreach (var result in results)
            foreach (var line in ReadinessChecks.FormatResult(result))
                io.WriteLine(line);

            var firstAdminOutstanding = results.Any(r => r.Name == "First admin" && r.Status != CheckStatus.Ok);
            var options = new List<(string Value, string Label)> { ("print", "Print effective configuration") };
            if (firstAdminOutstanding)
                options.Add(("invite", "Reissue the first-run invite"));
            options.Add(("start", "Start DbDataSync"));
            options.Add(("exit", "Exit"));

            var choice = prompt.Choice("What next?", options, "exit");
            switch (choice)
            {
                case "print":
                    PrintEffectiveConfiguration(context.Configuration, io);
                    break;
                case "invite":
                    InviteCommand.Run(["--repo", root]);
                    break;
                case "start":
                    return await ServeCommand.RunAsync(["--repo", root]);
                default:
                    return 0;
            }
        }
    }

    /// <summary>
    /// Every <c>DbDataSync:*</c> key the merged configuration resolves to — file, then environment,
    /// then command line, the same precedence <c>config check</c> and the running host use. Nothing here can
    /// embed a real credential (<see cref="DbDataSyncConfigFile.SetValue"/> refuses one, and the state
    /// password lives only in <see cref="SecretStore"/>), but an operator can still set one through an
    /// environment variable this command has no say over, so a value that looks like it carries one is
    /// redacted rather than trusted to be safe by construction.
    /// </summary>
    private static void PrintEffectiveConfiguration(IConfiguration configuration, IPromptIo io)
    {
        var entries = configuration.AsEnumerable()
            .Where(kv => kv.Value is not null
                && kv.Key.StartsWith("DbDataSync:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in entries)
            io.WriteLine($"{key} = {Redact(value!)}");
    }

    private static string Redact(string value)
    {
        var index = value.IndexOf("password=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return value;

        var end = value.IndexOf(';', index);
        return end < 0
            ? value[..index] + "Password=****"
            : value[..index] + "Password=****" + value[end..];
    }
}
