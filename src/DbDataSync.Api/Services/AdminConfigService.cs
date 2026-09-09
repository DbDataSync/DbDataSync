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
/// The admin config screen's backend (phase 81) — every <c>DbDataSync:*</c> key CONFIG.md documents,
/// its live effective value, where that value actually came from, and what this screen can do about
/// it.
/// <para>
/// <b>Only the flat, top-level <c>DbDataSync:&lt;Key&gt;</c> keys are file-writable</b> —
/// <see cref="DbDataSyncConfigFile.SetValue"/>'s own doc comment already says its text-editing writer
/// "does not handle arbitrary YAML nesting on the write side", and phase 79 explicitly judged that
/// shape "sufficient for... phase 81's admin screen" rather than extending it. <c>DbDataSync:Auth:*</c>
/// and <c>DbDataSync:Auth:Passkeys:*</c> are one or two levels deeper, and <c>Origins</c> is an array —
/// none of which the writer can address — so those rows are shown for the same real-value/real-source
/// honesty as everything else, but are never editable or adoptable here. See this phase's
/// retrospective for the full reasoning.
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
        string Key, string Description, bool SupportsWrite, bool IsSecret = false, string? Unit = null);

    private static readonly IReadOnlyList<KeyDefinition> Keys =
    [
        new("DbDataSync:RepoRoot",
            "Git-tracked config store root. This is how dbdatasync.config.yaml itself is found, so a " +
            "value inside that same file could never relocate it — not editable here.",
            SupportsWrite: false),
        new("DbDataSync:Url",
            "Console/API bind address. Only `dbdatasync serve`/`dbdatasync health` resolve this themselves " +
            "before translating it to Kestrel's --urls; the raw `dotnet run` entry point ignores it.",
            SupportsWrite: true),
        new("DbDataSync:StateDbPath",
            "The SQLite state file. Ignored when StateEngine is MsSql or Postgres.",
            SupportsWrite: true),
        new("DbDataSync:StateEngine",
            "Which database backs the state store. Sqlite, MsSql or Postgres — run history, the work " +
            "queue, watermarks, users and sessions all live here. An unrecognized value refuses to " +
            "start rather than silently falling back to Sqlite.",
            SupportsWrite: true),
        new("DbDataSync:StateConnectionString",
            "How to reach StateEngine when it isn't Sqlite. Never carries a password — set that " +
            "separately, below.",
            SupportsWrite: true, IsSecret: true),
        new("DbDataSync:TaskRunnerDllPath",
            "Where DbDataSync.TaskRunner.dll is. Resolved automatically for a normal install or container.",
            SupportsWrite: true),
        new("DbDataSync:StatePort",
            "The loopback-only runner-state listener's port. 0 binds an ephemeral one.",
            SupportsWrite: true),
        new("DbDataSync:RunRetentionDays",
            "Finished runs older than this are pruned hourly. 0 keeps forever.",
            SupportsWrite: true, Unit: "days"),
        new("DbDataSync:RunRetentionMaxPerMapping",
            "Most recent N finished runs kept, per table mapping. 0 = no cap.",
            SupportsWrite: true, Unit: "runs"),
        new("DbDataSync:RunPruningIntervalMinutes",
            "How often the retention sweep runs.",
            SupportsWrite: true, Unit: "minutes"),
        new("DbDataSync:ChangeCheckRetentionDays",
            "How long the scheduler's change-check history (phase 75) is kept. 0 keeps forever.",
            SupportsWrite: true, Unit: "days"),
        new("DbDataSync:NuGetSearchEnabled",
            "Whether the Libraries screen's search box may call the public NuGet index. Disable in an " +
            "air-gapped or locked-down deployment.",
            SupportsWrite: true),
        new("DbDataSync:Auth:Disabled",
            "Runs with no authentication at all. Nested under Auth, one level past what " +
            "dbdatasync.config.yaml's writer can address — set via environment variable or CLI flag.",
            SupportsWrite: false),
        new("DbDataSync:Auth:AdminGroup",
            "Windows group whose members are admins. Same nesting limit as Disabled, above.",
            SupportsWrite: false),
        new("DbDataSync:Auth:ViewerGroup",
            "Windows group whose members are viewers. Same nesting limit.",
            SupportsWrite: false),
        new("DbDataSync:Auth:Passkeys:RelyingPartyId",
            "Bare domain passkeys are scoped to. Same nesting limit.",
            SupportsWrite: false),
        new("DbDataSync:Auth:Passkeys:RelyingPartyName",
            "Shown in the OS passkey prompt. Same nesting limit.",
            SupportsWrite: false),
        new("DbDataSync:Auth:Passkeys:Origins",
            "Full origin URLs passkeys are valid from. An array — dbdatasync.config.yaml's writer only " +
            "ever writes one scalar per key, so this can never be file-writable.",
            SupportsWrite: false),
    ];

    public IReadOnlyList<AdminConfigEntry> List() => Keys.Select(ToEntry).ToList();

    public AdminConfigEntry? Get(string key)
    {
        var definition = Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        return definition is null ? null : ToEntry(definition);
    }

    /// <summary>
    /// Writes one top-level key into dbdatasync.config.yaml and commits it — the same call whether this
    /// is editing an already-file-sourced value or "adopting" one that was not. Returns null for a key
    /// this screen does not know, or that is not file-writable (see the class doc comment).
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

        // Every writable key today is exactly two segments (DbDataSync:X) — see the class doc comment
        // for why nothing deeper is ever in this list.
        var localKey = definition.Key["DbDataSync:".Length..];
        DbDataSyncConfigFile.SetValue(apiOptions.RepoRoot, "DbDataSync", localKey, value);
        git.CommitChanges(
            [DbDataSyncConfigFile.PathIn(apiOptions.RepoRoot)], $"Set '{definition.Key}' in dbdatasync.config.yaml", author);
        restartRequired.Touch();

        return ToEntry(definition);
    }

    /// <summary>
    /// Sets StateConnectionString's password through the same store <c>dbdatasync config secret set</c> writes
    /// to — this screen and the CLI command are two doors onto the same store, not two stores. The only
    /// key this applies to today; see CONFIG.md's "Secrets" section.
    /// </summary>
    public bool SetStateConnectionSecret(string key, string value)
    {
        if (!string.Equals(key, "DbDataSync:StateConnectionString", StringComparison.OrdinalIgnoreCase))
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
            definition.Description, definition.Unit);
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
        "DbDataSync:RepoRoot" => apiOptions.RepoRoot,
        "DbDataSync:Url" => null,
        "DbDataSync:StateDbPath" => apiOptions.StateDbPath,
        "DbDataSync:StateEngine" => apiOptions.StateEngine,
        "DbDataSync:StateConnectionString" => apiOptions.StateConnectionString,
        "DbDataSync:TaskRunnerDllPath" => apiOptions.TaskRunnerDllPath,
        "DbDataSync:StatePort" => apiOptions.StatePort.ToString(),
        "DbDataSync:RunRetentionDays" => apiOptions.RunRetentionDays?.ToString() ?? "0",
        "DbDataSync:RunRetentionMaxPerMapping" => apiOptions.RunRetentionMaxPerMapping?.ToString() ?? "0",
        "DbDataSync:RunPruningIntervalMinutes" => ((int)apiOptions.RunPruningInterval.TotalMinutes).ToString(),
        "DbDataSync:ChangeCheckRetentionDays" => apiOptions.ChangeCheckRetentionDays?.ToString() ?? "0",
        "DbDataSync:NuGetSearchEnabled" => apiOptions.NuGetSearchEnabled ? "true" : "false",
        "DbDataSync:Auth:Disabled" => authOptions.Disabled ? "true" : "false",
        "DbDataSync:Auth:AdminGroup" => authOptions.AdminGroup,
        "DbDataSync:Auth:ViewerGroup" => authOptions.ViewerGroup,
        "DbDataSync:Auth:Passkeys:RelyingPartyId" => passkeyOptions.RelyingPartyId,
        "DbDataSync:Auth:Passkeys:RelyingPartyName" => passkeyOptions.RelyingPartyName,
        "DbDataSync:Auth:Passkeys:Origins" => string.Join("; ", passkeyOptions.Origins),
        _ => null,
    };

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
        "DbDataSync:StateEngine" => ApiOptions.DefaultStateEngine,
        "DbDataSync:StatePort" => ApiOptions.DefaultStatePort.ToString(),
        "DbDataSync:RunRetentionDays" => ApiOptions.DefaultRunRetentionDays.ToString(),
        "DbDataSync:RunRetentionMaxPerMapping" => ApiOptions.DefaultRunRetentionMaxPerMapping.ToString(),
        "DbDataSync:RunPruningIntervalMinutes" => ApiOptions.DefaultRunPruningIntervalMinutes.ToString(),
        "DbDataSync:ChangeCheckRetentionDays" => ApiOptions.DefaultChangeCheckRetentionDays.ToString(),
        "DbDataSync:NuGetSearchEnabled" => ApiOptions.DefaultNuGetSearchEnabled ? "true" : "false",
        _ => null,
    };
}

/// <param name="Key">The full <c>DbDataSync:*</c> key, colon-separated, matching CONFIG.md exactly.</param>
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
/// <param name="Masked">True only for StateConnectionString, when <see cref="Value"/> or
/// <see cref="RunningValue"/> (independently) contains an embedded credential — that one is withheld,
/// never sent.</param>
/// <param name="Unit">What a numeric <see cref="Value"/>/<see cref="RunningValue"/> is counted in
/// ("days", "minutes", "runs") — null for a key that isn't a plain magnitude (a path, an engine name,
/// a boolean). The screen only renders it beside a value that's actually numeric, so a key with a unit
/// but an unset/non-numeric value shows no pill either.</param>
public sealed record AdminConfigEntry(
    string Key, string? Value, string? RunningValue, string Source, bool Editable, bool CanAdopt, bool CanReset,
    string? DefaultValue, bool Masked, string Description, string? Unit);
