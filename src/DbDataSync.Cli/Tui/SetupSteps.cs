using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;

namespace DbDataSync.Cli.Tui;

/// <summary>
/// The UI-free half of every <c>dbdatasync setup</c> section — everything <c>SetupCommand</c>'s
/// walk-through used to do inline between <c>Prompt</c> calls (<c>SetValue</c>/<c>SecretStore.Store</c>
/// calls, a driver install, a connection test), now callable with plain values so a test exercises it
/// directly rather than needing to drive a TUI to prove a config file or secret got written correctly.
/// <see cref="Tui.SetupScreen"/>'s tabs are thin: read their own controls, call one of these, show the
/// result — the same reason phase 82 kept a certificate's issue-and-bind logic separate from its
/// prompt.
/// </summary>
internal static class SetupSteps
{
    /// <param name="Warning">Whether <see cref="Message"/> should be shown as a warning rather than a
    /// plain confirmation — the same distinction the console flow drew between <c>io.WriteLine</c> and
    /// a line prefixed "Warning:"/"Could not connect yet:".</param>
    internal readonly record struct StepResult(bool Warning, string Message);

    /// <summary>
    /// SQLite needs nothing (the default, no separate server); SQL Server/PostgreSQL get their
    /// connection string written and a real connect attempted — reported back rather than trusted,
    /// exactly like the console step did (<c>SetupCommand.WalkThroughAsync</c>'s old step 3).
    /// </summary>
    /// <param name="password">Null means "leave the stored secret alone" — the tabbed form pre-fills
    /// nothing into a password field for an already-configured install, so a blank field on Save must
    /// not overwrite a real password with an empty one. Only a non-null value (including "") is
    /// written.</param>
    internal static StepResult ApplyStateDatabase(string root, string engine, string? connectionString, string? password)
    {
        if (engine == StateEngineIds.Sqlite)
            return new StepResult(false, "Using SQLite — nothing else to configure.");

        DbDataSyncConfigFile.SetValue(root, "DbDataSync", "StateEngine", engine);
        DbDataSyncConfigFile.SetValue(root, "DbDataSync", "StateConnectionString", connectionString ?? "");
        if (password is not null)
            new SecretStore("DbDataSync", true).Store(SecretRefs.ForAppSetting("stateConnectionString"), password);

        // Installing the library itself is skipped here — before phase 109g lands,
        // Microsoft.Data.SqlClient and Npgsql are still hard references, so there is nothing this step
        // would need to restore. `dbdatasync config check` still reports whether the connection opens.
        try
        {
            StateDatabase.FromOptions(
                engine, Path.Combine(root, "state.db"), connectionString, new SecretStore("DbDataSync", true));
            return new StepResult(false, "Connected — schema is current.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.Data.Common.DbException)
        {
            return new StepResult(
                true, $"Could not connect yet: {ex.Message} You can fix this and re-check with `dbdatasync config check`.");
        }
    }

    /// <summary>
    /// <paramref name="noAuthConfirmed"/> is resolved by the caller (a hard-confirm dialog, exactly the
    /// "type ALLOW" guard the console flow used) before this is ever called — this method only decides
    /// what to write given that already-made decision, the same split <see cref="ApplyStateDatabase"/>
    /// draws between prompting and writing.
    /// </summary>
    internal static StepResult ApplyAuthentication(
        string root, string choice, string? relyingPartyId, string? url, string? adminGroup, string? viewerGroup,
        bool noAuthConfirmed)
    {
        if (choice == "passkeys")
        {
            var rp = string.IsNullOrEmpty(relyingPartyId) ? "localhost" : relyingPartyId;
            var origins = new List<string> { url ?? "" };
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", rp);
            DbDataSyncConfigFile.SetListValue(root, "DbDataSync:Auth:Passkeys", "Origins", origins);

            var problem = new PasskeyOptions
            {
                RelyingPartyId = rp,
                RelyingPartyName = "DbDataSync",
                Origins = origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
            }.Problem();
            return problem is not null
                ? new StepResult(true, $"Warning: {problem}")
                : new StepResult(false, "Passkeys configured.");
        }

        if (choice == "windows")
        {
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "AdminGroup", adminGroup ?? "");
            if (!string.IsNullOrEmpty(viewerGroup))
                DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "ViewerGroup", viewerGroup);
            return new StepResult(false, "Windows group authentication configured.");
        }

        // choice == "none"
        if (noAuthConfirmed)
        {
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth", "Disabled", "true");
            return new StepResult(true, "Authentication disabled — every request is accepted with no sign-in.");
        }

        DbDataSyncConfigFile.SetValue(root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", "localhost");
        return new StepResult(true, "Not disabled — falling back to passkeys at localhost.");
    }

    /// <summary>The console URL is the one field with no branching or validation of its own — General
    /// tab builds the final URL string (plain, or scheme-and-host composed for a non-localhost
    /// hostname) and this just writes it.</summary>
    internal static void ApplyConsoleUrl(string root, string url) =>
        DbDataSyncConfigFile.SetValue(root, "DbDataSync", "Url", url);

    /// <summary>
    /// The one driver with a starter template to drive end to end (phase 109d); the three built-ins
    /// need nothing, and "another engine" has no template to fill in, so Drivers tab points at the
    /// manual command instead of calling this.
    /// </summary>
    internal static async Task<StepResult> InstallMySqlDriverAsync(
        string root, string version,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        const string packageId = "MySqlConnector";
        var factoryType = KnownLibraries.TryGet(packageId)!;
        var package = new PackageRef(packageId, version);

        try
        {
            await installLibrary(root, packageId, [package], factoryType, null, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return new StepResult(true, $"Could not install '{packageId}': {ex.Message}");
        }

        var driverDir = Path.Combine(root, "drivers", "mysql");
        var yamlPath = Path.Combine(driverDir, DriverLoader.DescriptorFileName);
        if (File.Exists(yamlPath))
            return new StepResult(true, $"'{yamlPath}' already exists — left untouched.");

        Directory.CreateDirectory(driverDir);
        var knownDriver = KnownDrivers.TryGetById("mysql.generic")!;
        var yaml = KnownDrivers.Render(knownDriver, "mysql", "MySQL / MariaDB", packageId);
        await File.WriteAllTextAsync(yamlPath, yaml);
        return new StepResult(
            false, $"Installed '{packageId}' and wrote '{yamlPath}'. Review the type map before relying on it.");
    }

    /// <summary>Printed above a service-install command whenever <see cref="Environment.ProcessPath"/>
    /// is under the user profile (phase 123) — empty when it isn't, so a caller can just splice this in
    /// rather than branching on it separately.</summary>
    internal static IReadOnlyList<string> MachineWideToolInstallBlock(bool isUnderUserProfile)
    {
        if (!isUnderUserProfile)
            return [];

        return
        [
            "This tool is installed in a user profile — install it machine-wide first:",
            OperatingSystem.IsWindows()
                ? $"    dotnet tool install --tool-path \"{CliOptions.DefaultToolDir}\" DbDataSync"
                : $"    sudo dotnet tool install --tool-path {CliOptions.DefaultToolDir} DbDataSync",
            OperatingSystem.IsWindows()
                ? $"    \"{CliOptions.DefaultToolDir}\\dbdatasync.exe\" tool install"
                : $"    sudo {CliOptions.DefaultToolDir}/dbdatasync tool install",
        ];
    }

    /// <summary>
    /// Neither Windows nor Linux service registration is orchestrated here — both need real elevation
    /// this process cannot assume it has — so this only computes the exact command to run, the same
    /// "print rather than guess" choice phase 51 made.
    /// </summary>
    internal static IReadOnlyList<string> WindowsServiceInstallInstructions(string root, string url, string account)
    {
        List<string> lines = ["Run this in an elevated prompt to finish:"];
        lines.AddRange(MachineWideToolInstallBlock(CliOptions.IsUnderUserProfile(Environment.ProcessPath)));
        lines.Add($"    dbdatasync service install --repo \"{root}\" --url {url} --account {account}");
        return lines;
    }

    internal static IReadOnlyList<string> SystemdServiceInstallInstructions(string root, string url, string user)
    {
        List<string> lines = ["Run this as root to finish:"];
        lines.AddRange(MachineWideToolInstallBlock(CliOptions.IsUnderUserProfile(Environment.ProcessPath)));
        lines.Add($"    sudo dbdatasync service install --repo \"{root}\" --url {url} --user {user}");
        return lines;
    }

    internal static IReadOnlyList<string> WindowsCertificateInstructions(string certChoice, string? host) =>
    [
        "Run the matching command, then bind the thumbprint it prints:",
        certChoice switch
        {
            "self-signed" => $"    dbdatasync config cert new-self-signed --dns {host ?? "localhost"}",
            "enroll" => "    dbdatasync config cert enroll --template <template>",
            _ => "    dbdatasync config cert bind --thumbprint <thumbprint>",
        },
    ];

    internal static IReadOnlyList<string> PemCertificateInstructions(string root, string certPath, string keyPath) =>
    [
        "Run this to finish:",
        $"    dbdatasync config cert use-pem --cert \"{certPath}\" --key \"{keyPath}\" --repo \"{root}\"",
    ];

    /// <summary>Phase 130, tier 2 — printed rather than invoked, the same choice phase 113 made for
    /// every certificate step in this walk-through ("Consistency with the rest of that walk-through's
    /// certificate handling won out over exploiting the one case where direct invocation was
    /// possible"). The trust caveat is stated here, not only in docs — an operator choosing this option
    /// should not discover it by having a browser refuse the console afterward.</summary>
    internal static IReadOnlyList<string> SelfSignedCertificateInstructions(string root) =>
    [
        "Run this to finish:",
        $"    dbdatasync config cert new-self-signed --repo \"{root}\"",
        "",
        "Every client reaching this console will need to trust this certificate once — there is no CA " +
        "behind it. A daily background check renews it automatically before it expires (a restart is " +
        "still needed to pick up a renewal).",
    ];

    /// <summary>One commit for the whole session, however many sections were saved — the same "one
    /// clean commit" shape the console walk-through's own finish step produced.</summary>
    internal static void CommitChanges(string root) =>
        new GitCommitService(root).CommitChanges(
            [DbDataSyncConfigFile.PathIn(root)], "dbdatasync setup", CurrentUser.SystemAuthor);

    /// <summary>
    /// Every <c>DbDataSync:*</c> key the merged configuration resolves to — file, then environment,
    /// then command line, the same precedence <c>config check</c> and the running host use. Nothing
    /// here can embed a real credential (<see cref="DbDataSyncConfigFile.SetValue"/> refuses one, and
    /// the state password lives only in <see cref="ClrKernel.Core.Secrets.SecretStore"/>), but an
    /// operator can still set one through an environment variable this command has no say over, so a
    /// value that looks like it carries one is redacted rather than trusted to be safe by construction.
    /// </summary>
    internal static IReadOnlyList<string> EffectiveConfigurationLines(IConfiguration configuration) =>
        configuration.AsEnumerable()
            .Where(kv => kv.Value is not null
                && kv.Key.StartsWith("DbDataSync:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} = {Redact(kv.Value!)}")
            .ToList();

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
