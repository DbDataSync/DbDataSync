using DataSync.Api.Auth;
using DataSync.Api.Configuration;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Core.Secrets;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace DataSync.Api.Services;

/// <summary>
/// The admin config screen's backend (phase 81) — every <c>DataSync:*</c> key CONFIG.md documents,
/// its live effective value, where that value actually came from, and what this screen can do about
/// it.
/// <para>
/// <b>Only the flat, top-level <c>DataSync:&lt;Key&gt;</c> keys are file-writable</b> —
/// <see cref="DataSyncConfigFile.SetValue"/>'s own doc comment already says its text-editing writer
/// "does not handle arbitrary YAML nesting on the write side", and phase 79 explicitly judged that
/// shape "sufficient for... phase 81's admin screen" rather than extending it. <c>DataSync:Auth:*</c>
/// and <c>DataSync:Auth:Passkeys:*</c> are one or two levels deeper, and <c>Origins</c> is an array —
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
    SecretStore secrets)
{
    private sealed record KeyDefinition(string Key, string Description, bool SupportsWrite, bool IsSecret = false);

    private static readonly IReadOnlyList<KeyDefinition> Keys =
    [
        new("DataSync:RepoRoot",
            "Git-tracked config store root. This is how datasync.config.yaml itself is found, so a " +
            "value inside that same file could never relocate it — not editable here.",
            SupportsWrite: false),
        new("DataSync:Url",
            "Console/API bind address. Only `datasync serve`/`datasync health` resolve this themselves " +
            "before translating it to Kestrel's --urls; the raw `dotnet run` entry point ignores it.",
            SupportsWrite: true),
        new("DataSync:StateDbPath",
            "The SQLite state file. Ignored when StateEngine is MsSql or Postgres.",
            SupportsWrite: true),
        new("DataSync:StateEngine",
            "Sqlite, MsSql or Postgres — which database backs run history, the work queue, watermarks, " +
            "users and sessions. An unrecognized value falls back to Sqlite rather than refusing to start.",
            SupportsWrite: true),
        new("DataSync:StateConnectionString",
            "How to reach StateEngine when it isn't Sqlite. Never carries a password — set that " +
            "separately, below.",
            SupportsWrite: true, IsSecret: true),
        new("DataSync:TaskRunnerDllPath",
            "Where DataSync.TaskRunner.dll is. Resolved automatically for a normal install or container.",
            SupportsWrite: true),
        new("DataSync:StatePort",
            "The loopback-only runner-state listener's port. 0 binds an ephemeral one.",
            SupportsWrite: true),
        new("DataSync:RunRetentionDays",
            "Finished runs older than this are pruned hourly. 0 keeps forever.",
            SupportsWrite: true),
        new("DataSync:RunRetentionMaxPerMapping",
            "Most recent N finished runs kept, per table mapping. 0 = no cap.",
            SupportsWrite: true),
        new("DataSync:RunPruningIntervalMinutes",
            "How often the retention sweep runs.",
            SupportsWrite: true),
        new("DataSync:ChangeCheckRetentionDays",
            "How long the scheduler's change-check history (phase 75) is kept. 0 keeps forever.",
            SupportsWrite: true),
        new("DataSync:Auth:Disabled",
            "Runs with no authentication at all. Nested under Auth, one level past what " +
            "datasync.config.yaml's writer can address — set via environment variable or CLI flag.",
            SupportsWrite: false),
        new("DataSync:Auth:AdminGroup",
            "Windows group whose members are admins. Same nesting limit as Disabled, above.",
            SupportsWrite: false),
        new("DataSync:Auth:ViewerGroup",
            "Windows group whose members are viewers. Same nesting limit.",
            SupportsWrite: false),
        new("DataSync:Auth:Passkeys:RelyingPartyId",
            "Bare domain passkeys are scoped to. Same nesting limit.",
            SupportsWrite: false),
        new("DataSync:Auth:Passkeys:RelyingPartyName",
            "Shown in the OS passkey prompt. Same nesting limit.",
            SupportsWrite: false),
        new("DataSync:Auth:Passkeys:Origins",
            "Full origin URLs passkeys are valid from. An array — datasync.config.yaml's writer only " +
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
    /// Writes one top-level key into datasync.config.yaml and commits it — the same call whether this
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

        // Every writable key today is exactly two segments (DataSync:X) — see the class doc comment
        // for why nothing deeper is ever in this list.
        var localKey = definition.Key["DataSync:".Length..];
        DataSyncConfigFile.SetValue(apiOptions.RepoRoot, "DataSync", localKey, value);
        git.CommitChanges(
            [DataSyncConfigFile.PathIn(apiOptions.RepoRoot)], $"Set '{definition.Key}' in datasync.config.yaml", author);

        return ToEntry(definition);
    }

    /// <summary>
    /// Sets StateConnectionString's password through the same store <c>datasync secret set</c> writes
    /// to — this screen and the CLI command are two doors onto the same store, not two stores. The only
    /// key this applies to today; see CONFIG.md's "Secrets" section.
    /// </summary>
    public bool SetStateConnectionSecret(string key, string value)
    {
        if (!string.Equals(key, "DataSync:StateConnectionString", StringComparison.OrdinalIgnoreCase))
            return false;

        secrets.Store(SecretRefs.ForAppSetting("stateConnectionString"), value);
        return true;
    }

    private AdminConfigEntry ToEntry(KeyDefinition definition)
    {
        var (source, raw) = ResolveSource(definition.Key);

        // For a key whose winning provider is the file, re-read the file directly rather than trusting
        // the provider's boot-time snapshot: IConfigurationRoot loads every provider once at startup, so
        // a save made through this very screen would otherwise not show up until the process restarts —
        // which is true of what the *running process* uses (the restart banner says so), but would also
        // make Save look like it silently did nothing. The file itself is cheap to re-read on every GET,
        // so "what is on disk right now" and "what an admin who just saved sees" can agree without
        // claiming the change is live anywhere else.
        string? value = source == "file"
            ? DataSyncConfigFile.Read(apiOptions.RepoRoot).GetValueOrDefault(definition.Key)
            : raw ?? DefaultFor(definition.Key);

        var masked = false;
        if (definition.IsSecret && source != "file" && value is not null && ConfigValidation.ContainsEmbeddedCredential(value))
        {
            masked = true;
            value = null;
        }

        var editable = definition.SupportsWrite && source == "file";
        var canAdopt = definition.SupportsWrite && source != "file" && !masked && value is not null;

        return new AdminConfigEntry(definition.Key, value, source, editable, canAdopt, masked, definition.Description);
    }

    /// <summary>
    /// Which provider supplies this key's effective value, and what it says — mirroring
    /// <c>IConfigurationRoot</c>'s own last-registered-wins resolution (see
    /// <c>DataSyncHost.InsertConfigFile</c>) rather than guessing at precedence a second way.
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
        DataSyncConfigFileProvider => "file",
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
        "DataSync:RepoRoot" => apiOptions.RepoRoot,
        "DataSync:Url" => null,
        "DataSync:StateDbPath" => apiOptions.StateDbPath,
        "DataSync:StateEngine" => apiOptions.StateEngine.ToString(),
        "DataSync:StateConnectionString" => apiOptions.StateConnectionString,
        "DataSync:TaskRunnerDllPath" => apiOptions.TaskRunnerDllPath,
        "DataSync:StatePort" => apiOptions.StatePort.ToString(),
        "DataSync:RunRetentionDays" => apiOptions.RunRetentionDays?.ToString() ?? "0",
        "DataSync:RunRetentionMaxPerMapping" => apiOptions.RunRetentionMaxPerMapping?.ToString() ?? "0",
        "DataSync:RunPruningIntervalMinutes" => ((int)apiOptions.RunPruningInterval.TotalMinutes).ToString(),
        "DataSync:ChangeCheckRetentionDays" => apiOptions.ChangeCheckRetentionDays?.ToString() ?? "0",
        "DataSync:Auth:Disabled" => authOptions.Disabled ? "true" : "false",
        "DataSync:Auth:AdminGroup" => authOptions.AdminGroup,
        "DataSync:Auth:ViewerGroup" => authOptions.ViewerGroup,
        "DataSync:Auth:Passkeys:RelyingPartyId" => passkeyOptions.RelyingPartyId,
        "DataSync:Auth:Passkeys:RelyingPartyName" => passkeyOptions.RelyingPartyName,
        "DataSync:Auth:Passkeys:Origins" => string.Join("; ", passkeyOptions.Origins),
        _ => null,
    };
}

/// <param name="Key">The full <c>DataSync:*</c> key, colon-separated, matching CONFIG.md exactly.</param>
/// <param name="Value">
/// The effective value, or null when there is none — either genuinely unset, or (StateConnectionString
/// only) withheld because <see cref="Masked"/> is true.
/// </param>
/// <param name="Source">One of <c>"file"</c>, <c>"environment variable"</c>, <c>"command line"</c>,
/// <c>"appsettings.json"</c> or <c>"default"</c>.</param>
/// <param name="Editable">True for a file-sourced key this screen can write.</param>
/// <param name="CanAdopt">True for a non-file-sourced key this screen can write, with a real value to
/// adopt and nothing to hide.</param>
/// <param name="Masked">True only for StateConnectionString, sourced from something other than the
/// file, that contains an embedded credential — the value is withheld, never sent.</param>
public sealed record AdminConfigEntry(
    string Key, string? Value, string Source, bool Editable, bool CanAdopt, bool Masked, string Description);
