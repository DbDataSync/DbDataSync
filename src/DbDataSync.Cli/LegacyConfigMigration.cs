using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Cli;

/// <summary>
/// Phase 164 regrouped the whole <c>DbDataSync:*</c> key surface (<c>Url</c> → <c>App:Url</c>,
/// <c>StateEngine</c> → <c>State:Engine</c>, ...) and replaced every bare boolean with a named mode
/// string (<c>Auth:Disabled: true</c> → <c>Auth:Network:Admin: loopback</c>, ...). An existing
/// <c>dbdatasync.config.yaml</c> using the old flat names must not silently stop being read the first
/// time this version starts — this is the one-time rewrite that heals it, run from
/// <see cref="ServeCommand.Prepare"/> (which <c>serve</c> and <c>setup</c> both call), the same place
/// the starter file itself is written from.
/// <para>
/// Environment variables and CLI flags using the old names are **not** migrated — there is nothing on
/// disk to rewrite, and both are typically set per-invocation rather than left stale for years the way
/// a committed file is.
/// </para>
/// </summary>
internal static class LegacyConfigMigration
{
    private sealed record Rename(
        string OldSection, string OldKey, string NewSection, string NewKey, Func<string, string>? Transform = null);

    private static readonly IReadOnlyList<Rename> Renames =
    [
        new("DbDataSync", "RepoRoot", "DbDataSync:App", "RepoRoot"),
        new("DbDataSync", "Url", "DbDataSync:App", "Url"),
        new("DbDataSync", "TaskRunnerDllPath", "DbDataSync:App", "TaskRunnerDllPath"),
        new("DbDataSync", "CliDllPath", "DbDataSync:App", "CliDllPath"),
        new("DbDataSync", "StateDbPath", "DbDataSync:State", "DbPath"),
        new("DbDataSync", "StateEngine", "DbDataSync:State", "Engine"),
        new("DbDataSync", "StateConnectionString", "DbDataSync:State", "ConnectionString"),
        new("DbDataSync", "StatePort", "DbDataSync:State", "Port"),
        new("DbDataSync", "RunRetentionDays", "DbDataSync:State:Retention", "RunDays"),
        new("DbDataSync", "RunRetentionMaxPerMapping", "DbDataSync:State:Retention", "RunMaxPerMapping"),
        new("DbDataSync", "RunPruningIntervalMinutes", "DbDataSync:State:Retention", "PruningIntervalMinutes"),
        new("DbDataSync", "ChangeCheckRetentionDays", "DbDataSync:State:Retention", "ChangeCheckDays"),
        new("DbDataSync", "NuGetSearchEnabled", "DbDataSync:Nuget:Search", "Mode", ToEnabledDisabled),
        new("DbDataSync", "NotesRichMarkdown", "DbDataSync:Notes", "MarkdownRenderer", ToRichBasic),
        new("DbDataSync", "SelfUpdateEnabled", "DbDataSync:Updates", "Mode", ToManualDisabled),
        new("DbDataSync", "SelfUpdateChannels", "DbDataSync:Updates", "Channels"),
        new("DbDataSync", "SelfUpdateDrainTimeoutSeconds", "DbDataSync:Updates", "DrainTimeoutSeconds"),
        new("DbDataSync", "SelfUpdateConfirmAfterSeconds", "DbDataSync:Updates", "ConfirmAfterSeconds"),
        // Deliberately narrower than the flag it replaces: Auth:Disabled trusted every request, from
        // anywhere, as Admin; Auth:Network:Admin can only ever be loopback or disabled. An operator
        // relying on remote unauthenticated admin access is narrowed rather than silently locked out —
        // named in this migration's own commit message so it isn't missed.
        new("DbDataSync:Auth", "Disabled", "DbDataSync:Auth:Network", "Admin", ToLoopbackDisabled),
        new("DbDataSync:Auth", "AdminGroup", "DbDataSync:Auth:Windows", "AdminGroup"),
        new("DbDataSync:Auth", "ViewerGroup", "DbDataSync:Auth:Windows", "ViewerGroup"),
    ];

    /// <summary>
    /// Rewrites every old-shaped key this root's <c>dbdatasync.config.yaml</c> still has, commits the
    /// result, and returns what changed (for the caller to log) — empty when there was nothing to do,
    /// which is the common case for a fresh install or one already on the new names.
    /// </summary>
    internal static IReadOnlyList<string> Migrate(string root)
    {
        var changes = new List<string>();

        foreach (var rename in Renames)
        {
            var oldFullKey = $"{rename.OldSection}:{rename.OldKey}";
            if (DbDataSyncConfigFile.Read(root).GetValueOrDefault(oldFullKey) is not { } oldValue)
                continue;

            var newValue = rename.Transform is null ? oldValue : rename.Transform(oldValue);
            var newFullKey = $"{rename.NewSection}:{rename.NewKey}";
            DbDataSyncConfigFile.SetValue(root, rename.NewSection, rename.NewKey, newValue);
            DbDataSyncConfigFile.RemoveValue(root, rename.OldSection, rename.OldKey);
            changes.Add($"{oldFullKey} -> {newFullKey}");
        }

        // Auth:Passkeys:Origins (an array) is superseded entirely: App:Url's own origin is always
        // implicitly trusted now, and App:AlternateUrls is purely additive. Any entry that doesn't
        // already match the (possibly just-migrated) App:Url is preserved there rather than silently
        // dropped; the common case (Origins was just [Url], written by SetupSteps) needs nothing kept.
        var origins = ReadOriginsArray(root);
        if (origins.Count > 0)
        {
            var url = DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:Url");
            var alternates = origins.Where(o => !string.Equals(o, url, StringComparison.OrdinalIgnoreCase)).ToList();
            if (alternates.Count > 0)
            {
                var existing = DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:AlternateUrls");
                var merged = string.IsNullOrEmpty(existing) ? alternates : [.. existing.Split(',', StringSplitOptions.TrimEntries), .. alternates];
                DbDataSyncConfigFile.SetValue(root, "DbDataSync:App", "AlternateUrls", string.Join(",", merged.Distinct()));
            }

            DbDataSyncConfigFile.RemoveListValue(root, "DbDataSync:Auth:Passkeys", "Origins");
            changes.Add("DbDataSync:Auth:Passkeys:Origins -> DbDataSync:App:Url (implicit) / DbDataSync:App:AlternateUrls");
        }

        if (changes.Count > 0)
        {
            new GitCommitService(root).CommitChanges(
                [DbDataSyncConfigFile.PathIn(root)],
                "Migrate dbdatasync.config.yaml to phase 164's grouped key names:\n\n" + string.Join('\n', changes),
                CurrentUser.SystemAuthor);
        }

        return changes;
    }

    private static IReadOnlyList<string> ReadOriginsArray(string root)
    {
        var config = DbDataSyncConfigFile.Read(root);
        var values = new List<string>();
        for (var i = 0; ; i++)
        {
            if (config.GetValueOrDefault($"DbDataSync:Auth:Passkeys:Origins:{i}") is not { } value)
                break;
            values.Add(value);
        }
        return values;
    }

    private static string ToEnabledDisabled(string old) => ParseBool(old) ? "enabled" : "disabled";
    private static string ToRichBasic(string old) => ParseBool(old) ? "rich" : "basic";
    private static string ToManualDisabled(string old) => ParseBool(old) ? "manual" : "disabled";
    private static string ToLoopbackDisabled(string old) => ParseBool(old) ? "loopback" : "disabled";

    private static bool ParseBool(string value) => bool.TryParse(value, out var parsed) && parsed;
}
