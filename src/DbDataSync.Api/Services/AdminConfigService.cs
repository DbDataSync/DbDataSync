using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace DbDataSync.Api.Services;

/// <summary>
/// The admin config screen's backend (phase 81) — every <c>DbDataSync:*</c> key docs/configuration.md documents,
/// its live effective value, where that value actually came from, and what this screen can do about
/// it.
/// <para>
/// Phase 164 reorganized the whole key surface into groups (<c>App:*</c>, <c>State:*</c>,
/// <c>Auth:Network:*</c>/<c>Auth:Windows:*</c>/<c>Auth:Passkeys:*</c>, <c>Updates:*</c>, <c>Nuget:Search:*</c>,
/// <c>Notes:*</c>) and replaced every bare boolean with a named mode string. That reorg also found that
/// the earlier "only flat, top-level keys are file-writable" claim was never actually true for
/// <see cref="DbDataSyncConfigFile.SetValue"/> — its text-editing writer tolerates a colon-containing
/// plain-scalar key or section (the exact mechanism <c>SetupSteps.cs</c> already relied on for the TUI
/// path), so nested keys under <c>Auth:Windows:*</c>/<c>Auth:Passkeys:*</c> are now genuinely
/// file-writable here too, not just display-only.
/// </para>
/// </summary>
public sealed class AdminConfigService(
    IConfiguration configuration,
    ApiOptions apiOptions,
    AuthOptions authOptions,
    PasskeyOptions passkeyOptions,
    GitCommitService git,
    SecretStore secrets,
    RestartRequiredState restartRequired)
{
    private sealed record KeyDefinition(
        string Key, string Description, bool SupportsWrite, bool IsSecret = false, string? Unit = null,
        string? Caution = null, IReadOnlyList<string>? AllowedValues = null);

    /// <summary>Lowercase enum member names, in declaration order — for a key whose value is a closed
    /// set backed by a real C# enum (a mode string), so the Admin screen can render a dropdown/toggle
    /// instead of a free-text box. Derived rather than hand-typed per key, so it can't drift from the
    /// enum <see cref="DefaultFor"/>/<see cref="DefaultValueFor"/> already parse against.
    /// <para>
    /// Deliberately not used for <c>State:Engine</c>: its id space is open (a custom
    /// <c>StateDialect</c> can be registered beyond the three built-ins), so constraining it to a fixed
    /// dropdown would be wrong, not just incomplete.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> AllowedValuesFor<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().Select(name => name.ToLowerInvariant()).ToList();

    private static readonly IReadOnlyList<KeyDefinition> Keys =
    [
        new("DbDataSync:App:RepoRoot",
            "Git-tracked config store root. This is how dbdatasync.config.yaml itself is found, so a " +
            "value inside that same file could never relocate it — not editable here.",
            SupportsWrite: false),
        new("DbDataSync:App:Url",
            "Console/API bind address. Only `dbdatasync serve`/`dbdatasync health` resolve this themselves " +
            "before translating it to Kestrel's --urls; the raw `dotnet run` entry point ignores it.",
            SupportsWrite: true),
        new("DbDataSync:App:AlternateUrls",
            "Every other origin this deployment is also reached at, beyond App:Url — comma- or " +
            "semicolon-separated. Purely additive: App:Url's own origin is always trusted for passkeys " +
            "without needing to be listed here too.",
            SupportsWrite: true),
        new("DbDataSync:App:TaskRunnerDllPath",
            "Where DbDataSync.TaskRunner.dll is. Resolved automatically for a normal install or container.",
            SupportsWrite: true),
        new("DbDataSync:State:DbPath",
            "The SQLite state file. Ignored when State:Engine is MsSql or Postgres.",
            SupportsWrite: true),
        new("DbDataSync:State:Engine",
            "Which database backs the state store. Sqlite, MsSql or Postgres — run history, the work " +
            "queue, watermarks, users and sessions all live here. An unrecognized value refuses to " +
            "start rather than silently falling back to Sqlite.",
            SupportsWrite: true),
        new("DbDataSync:State:ConnectionString",
            "How to reach State:Engine when it isn't Sqlite. Never carries a password — set that " +
            "separately, below.",
            SupportsWrite: true, IsSecret: true),
        new("DbDataSync:State:Port",
            "The loopback-only runner-state listener's port. 0 binds an ephemeral one.",
            SupportsWrite: true),
        new("DbDataSync:State:Retention:RunDays",
            "Finished runs older than this are pruned hourly. 0 keeps forever.",
            SupportsWrite: true, Unit: "days"),
        new("DbDataSync:State:Retention:RunMaxPerMapping",
            "Most recent N finished runs kept, per table mapping. 0 = no cap.",
            SupportsWrite: true, Unit: "runs"),
        new("DbDataSync:State:Retention:PruningIntervalMinutes",
            "How often the retention sweep runs.",
            SupportsWrite: true, Unit: "minutes"),
        new("DbDataSync:State:Retention:ChangeCheckDays",
            "How long the scheduler's change-check history (phase 75) is kept. 0 keeps forever.",
            SupportsWrite: true, Unit: "days"),
        new("DbDataSync:Nuget:Search:Mode",
            "Whether the Libraries screen's search box may call the public NuGet index (enabled/disabled). " +
            "Disable in an air-gapped or locked-down deployment.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<FeatureMode>()),
        new("DbDataSync:Notes:MarkdownRenderer",
            "Which renderer Notes use: basic (small, safe default) or rich (tables, task lists, strikethrough).",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<NotesRenderer>(),
            Caution:
                "Notes are written by one operator and shown in other people's sessions. The rich renderer is " +
                "defended the way the Docs viewer is — raw HTML is never interpreted, only http(s) and mailto " +
                "links are followed, and images show as links rather than loading — but it is a wider surface: " +
                "a convincingly crafted link, or a flaw in the library later. Leave it basic unless your team " +
                "needs tables in notes."),
        new("DbDataSync:Updates:Mode",
            "Whether, and how, an admin may update this installation from the Updates screen: manual or " +
            "disabled. Disabled by default: it replaces the code the service runs, as the service's own " +
            "account. Needs a systemd unit written by this version or later (`dbdatasync service install`); " +
            "Linux only for now.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<UpdatesMode>()),
        new("DbDataSync:Updates:Channels",
            "Which release channels the Updates screen may offer, comma-separated: stable, beta, snapshot. " +
            "A snapshot is a development build; its download is only checked against a checksum published " +
            "beside it.",
            SupportsWrite: true),
        new("DbDataSync:Updates:DrainTimeoutSeconds",
            "How long an update waits for running work to finish before restarting the service anyway. " +
            "Anything interrupted is reconciled at the next start.",
            SupportsWrite: true, Unit: "seconds"),
        new("DbDataSync:Updates:ConfirmAfterSeconds",
            "How long an updated version must have been serving before the update counts as having worked. " +
            "Until then, a restart rolls the update back.",
            SupportsWrite: true, Unit: "seconds"),
        new("DbDataSync:Auth:Network:Admin",
            "Trusts an unauthenticated request from loopback as Admin: loopback or disabled. There is no " +
            "\"from anywhere\" option for Admin — only Auth:Network:Viewer ever widens past loopback.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<AdminNetworkTrust>(),
            Caution:
                "Anyone who can reach this loopback address — any local account on a shared host, not only " +
                "the operator — gets Admin with no sign-in at all. Leave this disabled unless the deployment " +
                "is a single-operator box."),
        new("DbDataSync:Auth:Network:Viewer",
            "Trusts an unauthenticated request as Viewer: remote, loopback, or disabled. Remote trusts any " +
            "origin as Viewer; loopback restricts that to loopback only.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<ViewerNetworkTrust>(),
            Caution:
                "A Viewer can read replication state and configuration, just not change it. `remote` means " +
                "anyone who can reach this deployment at all gets that without signing in."),
        new("DbDataSync:Auth:Windows:Mode",
            "Whether Windows group authentication is allowed at all: enabled or disabled. An operator can " +
            "configure Auth:Windows:AdminGroup/ViewerGroup and still turn this off without clearing them.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<FeatureMode>()),
        new("DbDataSync:Auth:Windows:AdminGroup",
            "Windows group whose members are admins.",
            SupportsWrite: true),
        new("DbDataSync:Auth:Windows:ViewerGroup",
            "Windows group whose members are viewers.",
            SupportsWrite: true),
        new("DbDataSync:Auth:Passkeys:Mode",
            "Whether passkey sign-in/enrollment is allowed at all: enabled or disabled. An operator can " +
            "configure a relying-party id and still turn this off without clearing it.",
            SupportsWrite: true, AllowedValues: AllowedValuesFor<FeatureMode>()),
        new("DbDataSync:Auth:Passkeys:RelyingPartyId",
            "Bare domain passkeys are scoped to. Deliberately independent of App:Url — see " +
            "architecture/planning/todo/passkey-relying-party-migration.md for why changing this " +
            "invalidates every already-registered passkey, whatever sets it.",
            SupportsWrite: true),
        new("DbDataSync:Auth:Passkeys:RelyingPartyName",
            "Shown in the OS passkey prompt.",
            SupportsWrite: true),
    ];

    /// <summary>
    /// What the CLI's <c>config set</c> and <c>setup</c> need to know about a key this screen could write, read from the
    /// same catalog — so the three surfaces agree on which keys exist, what they default to and what to warn about, and a
    /// key added here appears on all of them. Null for an unknown key or one that is not file-writable.
    /// <paramref name="key"/> may omit the <c>DbDataSync:</c> prefix, as the file's own layout does.
    /// </summary>
    public static WritableConfigKey? Writable(string key)
    {
        var full = key.StartsWith("DbDataSync:", StringComparison.OrdinalIgnoreCase) ? key : $"DbDataSync:{key}";
        var definition = Keys.FirstOrDefault(k => string.Equals(k.Key, full, StringComparison.OrdinalIgnoreCase));
        return definition is { SupportsWrite: true }
            ? new WritableConfigKey(
                definition.Key, definition.Description, DefaultValueFor(definition.Key), definition.Caution,
                definition.AllowedValues)
            : null;
    }

    /// <summary>Every key <see cref="Writable"/> would return, for a usage message that lists them.</summary>
    public static IReadOnlyList<string> WritableKeyNames() =>
        Keys.Where(k => k.SupportsWrite).Select(k => k.Key["DbDataSync:".Length..]).ToList();

    public IReadOnlyList<AdminConfigEntry> List() => Keys.Select(ToEntry).ToList();

    public AdminConfigEntry? Get(string key)
    {
        var definition = Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        return definition is null ? null : ToEntry(definition);
    }

    /// <summary>
    /// Writes one key into dbdatasync.config.yaml and commits it — the same call whether this is editing an
    /// already-file-sourced value or "adopting" one that was not. Returns null for a key this screen does not
    /// know, or that is not file-writable (see the class doc comment).
    /// <para>
    /// <paramref name="author"/> is threaded in from the controller (<see cref="CurrentUser.Author"/>)
    /// rather than read from <c>IHttpContextAccessor</c> here — this service is a singleton with no
    /// request of its own, and the controller already has one per call.
    /// </para>
    /// </summary>
    public AdminConfigEntry? Set(string key, string value, GitAuthor author)
    {
        var definition = Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !definition.SupportsWrite)
            return null;

        // The section stays the fixed "DbDataSync" and everything past it — however many segments —
        // goes in as the key. DbDataSyncConfigFile.SetValue's text-editing writer tolerates a
        // colon-containing plain-scalar key (verified with a round-trip test), which is exactly the
        // mechanism SetupSteps.cs already relies on for the TUI path — this was never actually blocked
        // by the writer, only by this catalog choosing not to expose it before phase 164.
        var localKey = definition.Key["DbDataSync:".Length..];
        DbDataSyncConfigFile.SetValue(apiOptions.RepoRoot, "DbDataSync", localKey, value);
        git.CommitChanges(
            [DbDataSyncConfigFile.PathIn(apiOptions.RepoRoot)], $"Set '{definition.Key}' in dbdatasync.config.yaml", author);
        restartRequired.Touch();

        return ToEntry(definition);
    }

    /// <summary>
    /// Sets State:ConnectionString's password through the same store <c>dbdatasync config secret set</c> writes
    /// to — this screen and the CLI command are two doors onto the same store, not two stores. The only
    /// key this applies to today; see docs/configuration.md's "Secrets" section.
    /// </summary>
    public bool SetStateConnectionSecret(string key, string value)
    {
        if (!string.Equals(key, "DbDataSync:State:ConnectionString", StringComparison.OrdinalIgnoreCase))
            return false;

        secrets.Store(SecretRefs.ForAppSetting("stateConnectionString"), value);
        restartRequired.Touch();
        return true;
    }

    private AdminConfigEntry ToEntry(KeyDefinition definition)
    {
        // What this process actually loaded at startup and is running with right now — ApiOptions (and
        // its siblings) are resolved once, in DbDataSyncHost.Build, and never again, so reading straight
        // off them is the running value by construction: no second snapshot to keep in sync, and no way
        // for it to drift from what the process is really doing.
        var runningValue = DefaultFor(definition.Key);

        // The file is checked directly and unconditionally, never inferred from which IConfiguration
        // provider won at boot. That provider list is frozen at startup — DbDataSyncConfigFileProvider's
        // own Data dictionary is loaded once and never reloaded — so a key that started life sourced from
        // an environment variable or a CLI flag would keep reporting that as its source *forever*, even
        // after Set() (below) writes it into the file: an adopted key would look permanently un-adopted,
        // and a second edit to it would silently stop showing up. The file itself is cheap to re-read on
        // every GET, so "is this key in the file right now" is asked directly rather than trusted from a
        // snapshot that can go stale the moment this very class writes to it. This is what makes editing
        // an adopted value actually work — an admin can queue up several such edits before ever
        // restarting, each one independent of the last.
        var fileData = DbDataSyncConfigFile.Read(apiOptions.RepoRoot);
        var fileHasKey = fileData.TryGetValue(definition.Key, out var fileValue);

        string source;
        string? value;
        if (fileHasKey)
        {
            source = "file";
            value = fileValue;
        }
        else
        {
            (source, var raw) = ResolveSource(definition.Key);
            value = raw ?? runningValue;
        }

        var masked = false;
        if (definition.IsSecret)
        {
            // Configured and running are masked independently — a running process booted before this
            // key's value was ever put through the secret store (an old raw-password environment
            // variable, say) can carry a credential the current file no longer does, and each has to be
            // judged on what it actually contains rather than one flag standing in for both.
            if (source != "file" && value is not null && ConfigValidation.ContainsEmbeddedCredential(value))
            {
                masked = true;
                value = null;
            }
            if (runningValue is not null && ConfigValidation.ContainsEmbeddedCredential(runningValue))
            {
                masked = true;
                runningValue = null;
            }
        }

        var editable = definition.SupportsWrite && source == "file";
        var canAdopt = definition.SupportsWrite && source != "file" && !masked && value is not null;

        // Only offered once there's something to undo: a file-sourced key already sitting at its
        // application default has nothing for Reset to do.
        var defaultValue = DefaultValueFor(definition.Key);
        var canReset = editable && defaultValue is not null && !string.Equals(value, defaultValue, StringComparison.Ordinal);

        return new AdminConfigEntry(
            definition.Key, value, runningValue, source, editable, canAdopt, canReset, defaultValue, masked,
            definition.Description, definition.Unit, definition.Caution, definition.AllowedValues);
    }

    /// <summary>
    /// Which provider supplies this key's effective value, and what it says — mirroring
    /// <c>IConfigurationRoot</c>'s own last-registered-wins resolution (see
    /// <c>DbDataSyncHost.InsertConfigFile</c>) rather than guessing at precedence a second way.
    /// </summary>
    private (string Source, string? Value) ResolveSource(string key)
    {
        var root = (IConfigurationRoot)configuration;
        string? label = null;
        string? value = null;

        foreach (var provider in root.Providers)
        {
            if (!provider.TryGet(key, out var found))
                continue;

            label = LabelFor(provider);
            value = found;
        }

        return label is null ? ("default", null) : (label, value);
    }

    private static string LabelFor(IConfigurationProvider provider) => provider switch
    {
        DbDataSyncConfigFileProvider => "file",
        CommandLineConfigurationProvider => "command line",
        EnvironmentVariablesConfigurationProvider => "environment variable",
        JsonConfigurationProvider => "appsettings.json",
        _ => provider.GetType().Name,
    };

    /// <summary>
    /// What this key resolves to when nothing configures it — read off the same live options objects
    /// (<see cref="ApiOptions"/>, <see cref="AuthOptions"/>, <see cref="PasskeyOptions"/>) the rest of
    /// the process uses, rather than a second copy of each default that could drift from theirs.
    /// </summary>
    private string? DefaultFor(string key) => key switch
    {
        "DbDataSync:App:RepoRoot" => apiOptions.RepoRoot,
        "DbDataSync:App:Url" => apiOptions.Url,
        "DbDataSync:App:AlternateUrls" => string.Join(", ", apiOptions.AlternateUrls),
        "DbDataSync:App:TaskRunnerDllPath" => apiOptions.TaskRunnerDllPath,
        "DbDataSync:State:DbPath" => apiOptions.StateDbPath,
        "DbDataSync:State:Engine" => apiOptions.StateEngine,
        "DbDataSync:State:ConnectionString" => apiOptions.StateConnectionString,
        "DbDataSync:State:Port" => apiOptions.StatePort.ToString(),
        "DbDataSync:State:Retention:RunDays" => apiOptions.RunRetentionDays?.ToString() ?? "0",
        "DbDataSync:State:Retention:RunMaxPerMapping" => apiOptions.RunRetentionMaxPerMapping?.ToString() ?? "0",
        "DbDataSync:State:Retention:PruningIntervalMinutes" => ((int)apiOptions.RunPruningInterval.TotalMinutes).ToString(),
        "DbDataSync:State:Retention:ChangeCheckDays" => apiOptions.ChangeCheckRetentionDays?.ToString() ?? "0",
        "DbDataSync:Nuget:Search:Mode" => Lower(apiOptions.NugetSearchMode),
        "DbDataSync:Notes:MarkdownRenderer" => Lower(apiOptions.NotesRenderer),
        "DbDataSync:Updates:Mode" => Lower(apiOptions.SelfUpdateMode),
        "DbDataSync:Updates:Channels" => string.Join(",", apiOptions.SelfUpdateChannels.Select(c => c.ToString().ToLowerInvariant())),
        "DbDataSync:Updates:DrainTimeoutSeconds" => ((int)apiOptions.SelfUpdateDrainTimeout.TotalSeconds).ToString(),
        "DbDataSync:Updates:ConfirmAfterSeconds" => ((int)apiOptions.SelfUpdateConfirmAfter.TotalSeconds).ToString(),
        "DbDataSync:Auth:Network:Admin" => Lower(authOptions.NetworkAdmin),
        "DbDataSync:Auth:Network:Viewer" => Lower(authOptions.NetworkViewer),
        "DbDataSync:Auth:Windows:Mode" => Lower(authOptions.WindowsMode),
        "DbDataSync:Auth:Windows:AdminGroup" => authOptions.AdminGroup,
        "DbDataSync:Auth:Windows:ViewerGroup" => authOptions.ViewerGroup,
        "DbDataSync:Auth:Passkeys:Mode" => Lower(passkeyOptions.Mode),
        "DbDataSync:Auth:Passkeys:RelyingPartyId" => passkeyOptions.RelyingPartyId,
        "DbDataSync:Auth:Passkeys:RelyingPartyName" => passkeyOptions.RelyingPartyName,
        _ => null,
    };

    /// <summary>Lowercase, matching how every mode-string setting is written in configuration
    /// (<c>enabled</c>/<c>disabled</c>/<c>loopback</c>/... — never PascalCase in the file, even though
    /// the .NET enum member is).</summary>
    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>
    /// The literal this key falls back to when nothing configures it at all — what Reset writes into
    /// the file. Deliberately not <see cref="DefaultFor"/>: that reads the *running* options object,
    /// which for a key that has always been file-sourced already reflects the file rather than the
    /// application's own fallback, so it can't tell "reset to factory" from "reset to whatever this
    /// happened to be at boot". These come from the same named constants <see cref="ApiOptions
    /// .FromConfiguration"/> itself falls back to, so the two can never quietly disagree.
    /// <para>
    /// Null for a key whose default is contextual rather than a fixed literal (RepoRoot, StateDbPath and
    /// TaskRunnerDllPath are all derived from the machine/working directory; Url and
    /// StateConnectionString have none at this layer) — Reset has nothing sensible to offer there, and
    /// says so by staying hidden rather than resetting to a value nobody chose.
    /// </para>
    /// </summary>
    private static string? DefaultValueFor(string key) => key switch
    {
        "DbDataSync:App:Url" => ApiOptions.DefaultUrl,
        "DbDataSync:State:Engine" => ApiOptions.DefaultStateEngine,
        "DbDataSync:State:Port" => ApiOptions.DefaultStatePort.ToString(),
        "DbDataSync:State:Retention:RunDays" => ApiOptions.DefaultRunRetentionDays.ToString(),
        "DbDataSync:State:Retention:RunMaxPerMapping" => ApiOptions.DefaultRunRetentionMaxPerMapping.ToString(),
        "DbDataSync:State:Retention:PruningIntervalMinutes" => ApiOptions.DefaultRunPruningIntervalMinutes.ToString(),
        "DbDataSync:State:Retention:ChangeCheckDays" => ApiOptions.DefaultChangeCheckRetentionDays.ToString(),
        "DbDataSync:Nuget:Search:Mode" => Lower(ApiOptions.DefaultNugetSearchMode),
        "DbDataSync:Notes:MarkdownRenderer" => Lower(ApiOptions.DefaultNotesRenderer),
        "DbDataSync:Updates:Mode" => Lower(ApiOptions.DefaultSelfUpdateMode),
        "DbDataSync:Updates:Channels" => ApiOptions.DefaultSelfUpdateChannels,
        "DbDataSync:Updates:DrainTimeoutSeconds" => ApiOptions.DefaultSelfUpdateDrainTimeoutSeconds.ToString(),
        "DbDataSync:Updates:ConfirmAfterSeconds" => ApiOptions.DefaultSelfUpdateConfirmAfterSeconds.ToString(),
        "DbDataSync:Auth:Network:Admin" => Lower(AdminNetworkTrust.Disabled),
        "DbDataSync:Auth:Network:Viewer" => Lower(ViewerNetworkTrust.Disabled),
        "DbDataSync:Auth:Windows:Mode" => Lower(FeatureMode.Enabled),
        "DbDataSync:Auth:Passkeys:Mode" => Lower(FeatureMode.Enabled),
        _ => null,
    };
}

/// <param name="Key">The full <c>DbDataSync:*</c> key, colon-separated, matching docs/configuration.md exactly.</param>
/// <param name="Value">
/// The <b>configured</b> value — what the winning provider says right now, re-read fresh for a
/// file-sourced key so a save this screen just made shows up immediately. This is what
/// <see cref="Editable"/> lets an admin change, independently of whether the running process has picked
/// it up yet. Null when there is none — either genuinely unset, or (StateConnectionString only)
/// withheld because <see cref="Masked"/> is true.
/// </param>
/// <param name="RunningValue">
/// What this process actually loaded at startup and is running with right now — frozen the moment
/// <c>ApiOptions</c> (or its siblings) was built, and never re-read. Differs from <see cref="Value"/>
/// exactly when a change is queued and not yet applied: a save through this screen, or an override that
/// moved a key from an environment variable/CLI flag/default into the file. The restart banner is what
/// closes that gap. Null under the same rules as <see cref="Value"/>.
/// </param>
/// <param name="Source">One of <c>"file"</c>, <c>"environment variable"</c>, <c>"command line"</c>,
/// <c>"appsettings.json"</c> or <c>"default"</c> — describes <see cref="Value"/>, not
/// <see cref="RunningValue"/>, which by definition came from whatever won at startup.</param>
/// <param name="Editable">True for a file-sourced key this screen can write.</param>
/// <param name="CanAdopt">True for a non-file-sourced key this screen can write, with a real value to
/// adopt and nothing to hide.</param>
/// <param name="CanReset">True for a file-sourced key with a known application default
/// (<see cref="DefaultValue"/>) that <see cref="Value"/> doesn't already equal — the inverse of
/// <see cref="CanAdopt"/>: putting the factory value back into the file rather than taking a non-file
/// one out of it.</param>
/// <param name="DefaultValue">What Reset would write, or null when this key's default is contextual
/// rather than a fixed literal (a path derived from the machine, say) — Reset has nothing to offer
/// there, which is exactly why <see cref="CanReset"/> is never true when this is null.</param>
/// <param name="Masked">True only for State:ConnectionString, when <see cref="Value"/> or
/// <see cref="RunningValue"/> (independently) contains an embedded credential — that one is withheld,
/// never sent.</param>
/// <param name="Unit">What a numeric <see cref="Value"/>/<see cref="RunningValue"/> is counted in
/// ("days", "minutes", "runs") — null for a key that isn't a plain magnitude (a path, an engine name,
/// a boolean). The screen only renders it beside a value that's actually numeric, so a key with a unit
/// but an unset/non-numeric value shows no pill either.</param>
/// <param name="Caution">A plain-language warning the screen shows beside this key, always, in both states — for a setting
/// whose "on" widens what an attacker or a mistake can reach. Null for every other key.</param>
/// <param name="AllowedValues">The complete, closed set of legal values, lowercase — for a mode-string
/// setting backed by a real C# enum, so the screen can render a dropdown/toggle instead of a free-text
/// box. Null for a setting with no fixed set (a path, a group name, a count) or an open one
/// (<c>State:Engine</c> — a custom dialect can be registered beyond the three built-ins).</param>
public sealed record AdminConfigEntry(
    string Key, string? Value, string? RunningValue, string Source, bool Editable, bool CanAdopt, bool CanReset,
    string? DefaultValue, bool Masked, string Description, string? Unit, string? Caution = null,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>A key <see cref="AdminConfigService.Writable"/> found: its full name, what it is for, the literal it falls back to
/// (null when contextual), the warning to show wherever it is changed (null for most), and — for a
/// mode-string setting backed by a real enum — the complete, closed set of legal values (null for an
/// open-ended setting).</summary>
public sealed record WritableConfigKey(
    string Key, string Description, string? DefaultValue, string? Caution, IReadOnlyList<string>? AllowedValues = null);
